using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HermesHyperV.Engine;

/// <summary>
/// Tiny static file server so EVERY Windows guest bootstraps its own
/// WindowsPilot install with zero caller steps: the unattend's second
/// FirstLogonCommand pulls prov_local.ps1 from here, and that script pulls
/// pilot-payload.zip — both from this one port. Like SandboxProxy it binds
/// only to the SandboxLAN vNIC (192.168.50.1:8898) and installs its own
/// scoped firewall allow rule at startup, so nothing else on the physical
/// LAN sees it and guests reach it without any extra setup.
///
/// Payload lives under %HYPERV_PILOT_DIR% (default C:\HyperV\Pilot):
///   prov_local.ps1       guest bootstrap script
///   pilot-payload.zip    extracted pilot build + wwwroot
/// Paths are served as /prov_local.ps1 or /pilot/prov_local.ps1 (prefix is
/// ignored). Missing files answer 404; the guest's ProvOnce task retries.
/// </summary>
public sealed class PilotServe(ILogger<PilotServe> log) : BackgroundService
{
    public const int Port = 8898;
    const string RuleName = "HermesHyperV Pilot Payload";

    static string PayloadDir =>
        Environment.GetEnvironmentVariable("HYPERV_PILOT_DIR") is { Length: > 0 } d
            ? d : Path.Combine(
                Environment.GetEnvironmentVariable("HYPERV_ROOT") ?? @"C:\HyperV", "Pilot");

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
                log.LogInformation("pilot payload server listening on 192.168.50.1:{Port} ({Dir})",
                    Port, PayloadDir);
                while (!ct.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(ct);
                    _ = Task.Run(() => Handle(client), CancellationToken.None);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                log.LogWarning("pilot-serve: bind/accept failed ({Err}); retrying in 5s", e.Message);
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
                     $"protocol=TCP localport={Port} remoteip=192.168.50.0/24");
        }
        catch { /* cosmetic */ }
    }

    static void RunNetsh(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("netsh.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
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
                var cs = client.GetStream();
                var line = await ReadLine(cs);
                if (line == null) return;                      // drain headers
                while (true) { var h = await ReadLine(cs); if (h == null || h.Length == 0) break; }

                var parts = line.Split(' ');
                if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    await Write(cs, 400, "text/plain", "need GET"u8.ToArray());
                    return;
                }
                var path = Uri.UnescapeDataString(parts[1].Split('?')[0]);
                if (path.StartsWith("/pilot/", StringComparison.OrdinalIgnoreCase))
                    path = path["/pilot/".Length..];
                var name = path.TrimStart('/');
                if (name.Length == 0) name = "prov_local.ps1";
                // simple containment: only plain file names
                if (name.Contains("..") || Path.IsPathRooted(name))
                {
                    await Write(cs, 400, "text/plain", "bad path"u8.ToArray());
                    return;
                }

                var file = Path.Combine(PayloadDir, name);
                if (!File.Exists(file))
                {
                    await Write(cs, 404, "text/plain", Encoding.UTF8.GetBytes($"not found: {name}"));
                    return;
                }
                var bytes = await File.ReadAllBytesAsync(file, CancellationToken.None);
                var ctype = name.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
                    ? "text/plain; charset=utf-8" : "application/octet-stream";
                await Write(cs, 200, ctype, bytes);
            }
            catch { /* guest hung up */ }
        }
    }

    static async Task Write(Stream s, int code, string ctype, byte[] body)
    {
        var head = new StringBuilder()
            .Append($"HTTP/1.1 {code} {(code == 200 ? "OK" : "NOK")}\r\n")
            .Append($"Content-Type: {ctype}\r\n")
            .Append($"Content-Length: {body.Length}\r\n")
            .Append("Cache-Control: no-cache\r\n\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
        await s.WriteAsync(body);
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
            if (sb.Length > 8192) return null;
        }
    }
}
