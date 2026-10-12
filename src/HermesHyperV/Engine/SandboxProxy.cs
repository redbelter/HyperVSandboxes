using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HermesHyperV.Engine;

/// <summary>
/// Tiny HTTP/CONNECT egress proxy so sandboxes can apt/pip/curl from the
/// internet. Guests live on an isolated internal switch BY DESIGN (no DHCP,
/// no NAT, nothing routable); this is the one controlled door out. It binds
/// ONLY to the SandboxLAN vNIC address (192.168.50.1), so other machines on
/// the physical LAN can't use it as an open proxy, and it enforces nothing —
/// these are disposable sandboxes, not a security boundary.
/// Guests get it preconfigured via the cloud-init seed (apt conf, curlrc,
/// pip.conf, profile.d). Windows Firewall lets ICMP through but drops guest
/// TCP to the host, so we also install a scoped allow rule (inbound TCP
/// 8899 from 192.168.50.0/24 only) at startup — the service is LocalSystem,
/// so no elevation is involved.
/// </summary>
public sealed class SandboxProxy(ILogger<SandboxProxy> log) : BackgroundService
{
    public const int Port = 8899;
    const string RuleName = "HermesHyperV Sandbox Proxy";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        EnsureFirewallRule();
        while (!ct.IsCancellationRequested)
        {
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Parse("192.168.50.1"), Port);
                listener.Start();
                log.LogInformation("sandbox egress proxy listening on 192.168.50.1:{Port}", Port);
                while (!ct.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(ct);
                    _ = Task.Run(() => Handle(client), CancellationToken.None);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                // vEthernet (SandboxLAN) may not have 192.168.50.1 yet — retry.
                log.LogWarning("proxy: bind/accept failed ({Err}); retrying in 5s", e.Message);
                listener?.Dispose();
                try { await Task.Delay(5000, ct); } catch { break; }
            }
        }
    }

    static void EnsureFirewallRule()
    {
        try
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{RuleName}\"");
            RunNetsh($"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow " +
                     "protocol=TCP localport=8899 remoteip=192.168.50.0/24");
        }
        catch { /* cosmetic — worst case guests can't reach the proxy */ }
    }

    static void RunNetsh(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("netsh.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,          // zero windows rule
            RedirectStandardOutput = true,
        };
        using var p = System.Diagnostics.Process.Start(psi);
        p?.WaitForExit(10000);
    }

    async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.SendBufferSize = client.ReceiveBufferSize = 64 * 1024;
                var cs = client.GetStream();
                var reqLine = await ReadLine(cs);
                if (reqLine == null) return;
                // drain the rest of the request headers (we forward them raw for plain HTTP)
                var headers = new List<string>();
                while (true)
                {
                    var h = await ReadLine(cs);
                    if (h == null || h.Length == 0) break;
                    headers.Add(h);
                }
                var parts = reqLine.Split(' ');
                if (parts.Length < 2) return;

                if (parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    var (host, port) = SplitHostPort(parts[1], 443);
                    var up = await Dial(host, port);
                    if (up == null) { await WriteAscii(cs, "HTTP/1.1 502 Bad Gateway\r\n\r\n"); return; }
                    using (up)
                    {
                        await WriteAscii(cs, "HTTP/1.1 200 Connection established\r\n\r\n");
                        var us = up.GetStream();
                        // when either direction ends we're done (dispose closes both)
                        await Task.WhenAny(cs.CopyToAsync(us), us.CopyToAsync(cs));
                    }
                    return;
                }

                // plain HTTP, absolute-form URI (apt/curl over http_proxy)
                if (!Uri.TryCreate(parts[1], UriKind.Absolute, out var uri) ||
                    uri.Scheme != "http")
                {
                    await WriteAscii(cs, "HTTP/1.1 400 Bad Request (proxy needs CONNECT or absolute URI)\r\n\r\n");
                    return;
                }
                var up2 = await Dial(uri.Host, uri.Port == -1 ? 80 : uri.Port);
                if (up2 == null) { await WriteAscii(cs, "HTTP/1.1 502 Bad Gateway\r\n\r\n"); return; }
                using (up2)
                {
                    var us2 = up2.GetStream();
                    var path = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
                    var head = new StringBuilder(parts[0]).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
                    foreach (var h in headers) head.Append(h).Append("\r\n");
                    head.Append("\r\n");
                    await WriteAscii(us2, head.ToString());
                    // concurrent: any remaining request bytes (body) go up, response comes down
                    await Task.WhenAny(cs.CopyToAsync(us2), us2.CopyToAsync(cs));
                }
            }
            catch { /* guest hung up / upstream died */ }
        }
    }

    static async Task<TcpClient?> Dial(string host, int port)
    {
        try
        {
            var c = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await c.ConnectAsync(host, port, timeout.Token);
            return c;
        }
        catch { return null; }
    }

    static (string, int) SplitHostPort(string s, int dflt)
    {
        var i = s.LastIndexOf(':');
        if (i > 0 && int.TryParse(s[(i + 1)..], out var p)) return (s[..i], p);
        return (s, dflt);
    }

    static async Task<string?> ReadLine(Stream s)
    {
        var one = new byte[1];
        var sb = new StringBuilder(256);
        while (true)
        {
            if (await s.ReadAsync(one, 0, 1) != 1) return sb.Length > 0 ? sb.ToString() : null;
            char c = (char)one[0];
            if (c == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append(c);
            if (sb.Length > 8192) return null;   // header flood guard
        }
    }

    static ValueTask WriteAscii(Stream s, string text) =>
        s.WriteAsync(Encoding.ASCII.GetBytes(text));
}
