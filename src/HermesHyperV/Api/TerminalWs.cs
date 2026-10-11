using System.Net.WebSockets;
using System.Text;
using HermesHyperV.Engine;

namespace HermesHyperV;

/// <summary>
/// Interactive terminal for Linux sandboxes: browser xterm.js ⇄ one WebSocket
/// ⇄ ssh.exe (service key, forced PTY) ⇄ guest bash. The service binds
/// loopback only and the guests are disposable sandboxes, so there is no
/// auth layer beyond "you can reach the port". Windows guests have no SSH
/// listener and PowerShell Direct has no PTY to bridge — they keep run_ps.
/// </summary>
public static class TerminalWs
{
    public static void MapTerminal(this WebApplication app)
    {
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        app.Map("/ws/term", async (HttpContext ctx, SandboxEngine engine) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            var name = ctx.Request.Query["name"].ToString();
            int rows = int.TryParse(ctx.Request.Query["rows"], out var r) ? Math.Clamp(r, 5, 200) : 40;
            int cols = int.TryParse(ctx.Request.Query["cols"], out var c) ? Math.Clamp(c, 20, 400) : 120;

            (string ip, string key)? target = null;
            string? setupError = null;
            try { target = await engine.LinuxTermTargetAsync(name, CancellationToken.None); }
            catch (Exception e) { setupError = e.Message; }

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            if (target is null)
            {
                await socket.SendAsync(new ArraySegment<byte>(
                    Encoding.UTF8.GetBytes($"\r\n\x1b[31m{setupError}\x1b[0m\r\n")),
                    WebSocketMessageType.Text, true, CancellationToken.None);
                try { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "no target", CancellationToken.None); }
                catch { }
                return;
            }

            // -tt forces a remote PTY (raw echo + job control, no local console).
            // stty sets the pty size first, then exec replaces the shell — the
            // guest never keeps an extra process per browser tab.
            var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                   "OpenSSH", "ssh.exe");
            var psi = new System.Diagnostics.ProcessStartInfo(ssh,
                $"-tt -i \"{target.Value.key}\" -o BatchMode=yes -o LogLevel=ERROR " +
                $"-o StrictHostKeyChecking=no " +
                $"-o UserKnownHostsFile=NUL -o ServerAliveInterval=15 -o ConnectTimeout=8 " +
                $"sandbox@{target.Value.ip} \"stty rows {rows} cols {cols} 2>/dev/null; exec bash -l\"")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,   // zero windows rule
            };
            System.Diagnostics.Process proc;
            try { proc = System.Diagnostics.Process.Start(psi)!; }
            catch (Exception e)
            {
                await socket.SendAsync(new ArraySegment<byte>(
                    Encoding.UTF8.GetBytes($"\r\n\x1b[31mfailed to start ssh: {e.Message}\x1b[0m\r\n")),
                    WebSocketMessageType.Text, true, CancellationToken.None);
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "ssh spawn failed", CancellationToken.None); }
                catch { }
                return;
            }

            using var cts = new CancellationTokenSource();
            var sendLock = new SemaphoreSlim(1, 1);   // WebSocket: one send at a time

            async Task Pump(Stream src)
            {
                var buf = new byte[8192];
                try
                {
                    while (true)
                    {
                        int n = await src.ReadAsync(buf, CancellationToken.None);
                        if (n <= 0) break;
                        await sendLock.WaitAsync();
                        try
                        {
                            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                                await socket.SendAsync(new ArraySegment<byte>(buf, 0, n),
                                    WebSocketMessageType.Binary, true, CancellationToken.None);
                        }
                        finally { sendLock.Release(); }
                    }
                }
                catch { }
            }
            var outPump = Pump(proc.StandardOutput.BaseStream);
            var errPump = Pump(proc.StandardError.BaseStream);

            try
            {
                var buf = new byte[16384];
                while (socket.State == WebSocketState.Open && !proc.HasExited)
                {
                    var res = await socket.ReceiveAsync(buf, CancellationToken.None);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                    if (res.Count > 0)
                    {
                        await proc.StandardInput.BaseStream.WriteAsync(buf, 0, res.Count);
                        await proc.StandardInput.BaseStream.FlushAsync();
                    }
                }
            }
            catch { }

            cts.Cancel();
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            try { await Task.WhenAny(Task.WhenAll(outPump, errPump), Task.Delay(1500)); } catch { }
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "end", CancellationToken.None);
            }
            catch { }
            proc.Dispose();
            sendLock.Dispose();
        });
    }
}
