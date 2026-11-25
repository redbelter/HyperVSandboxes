using System.Text;
using System.Text.Json;

namespace HermesHyperV.Engine;

/// <summary>
/// Deterministic stand-in for the hidden PowerShell children, used when
/// HYPERV_FAKE_PS=1 (CI boxes without Hyper-V). Mirrors the exact JSON
/// contract the real scripts emit: one {ok,data,error} result, plus the
/// per-action fields the engine reads. Records every request so tests can
/// assert what the engine actually asked the scripts to do.
///
/// List/delete/purge are STATEFUL (they read/write VM folders under the
/// request's vmroot), so a full create -> list -> exec -> delete cycle works
/// end-to-end against this fake — that's what tests/smoke.py exercises on CI.
/// </summary>
public sealed class FakeRunner : IPowerShellRunner
{
    public sealed record Call(string Script, JsonElement Request);
    public List<Call> Calls { get; } = [];
    readonly object _gate = new();

    public int CallCount { get { lock (_gate) return Calls.Count; } }

    public List<JsonElement> CallsFor(string script)
    {
        lock (_gate) return Calls.Where(c => c.Script == script).Select(c => c.Request).ToList();
    }

    public Task<PsResult> RunScriptAsync(string resourceName, object? request, int timeoutSeconds, CancellationToken ct = default)
    {
        var req = request is null ? Parse("{}") : JsonSerializer.SerializeToElement(request);
        lock (_gate) Calls.Add(new Call(resourceName, req));
        var action = req.TryGetProperty("action", out var a) ? a.GetString() : resourceName;
        JsonElement[] data = resourceName switch
        {
            "vm_lifecycle" => action switch
            {
                "status" => [Parse("""
                    {"root":"C:\\HyperV\\VMs","hypervisor_present":true,
                     "disks":{"C:":{"total_gb":100,"free_gb":50}},
                     "isos":["x.iso"],"vmms_service":"running"}
                    """)],
                "list" => ListRows(req),
                "diag" => [Parse("""
                    {"events":[{"id":256,"t":"2026-01-01T10:00:00","log":"Worker-Admin","level":"Informational","msg":"VM configured"}]}
                    """)],
                "editions" => [Parse("""
                    {"index":1,"name":"Windows Server 2025 Datacenter Evaluation (Desktop Experience)"}
                    """)],
                "linux_create" => [LinuxCreate(req)],
                "delete" => [DeleteVm(req)],
                "purge" => [Purge(req)],
                "power" => [JsonSerializer.SerializeToElement(new
                {
                    name = Get(req, "name"),
                    state = Get(req, "want") == "stop" ? "Off" : "Running",
                })],
                _ => [Parse("""{"state":"Running","name":"mcp-x","deleted":true}""")],
            },
            "guest_exec" => [ExecResult(req)],
            "linux_ssh" => [SshResult(req)],
            _ => [Parse("""{"step":"done"}""")],   // golden_image / linux_golden / child_disk
        };
        return Task.FromResult(new PsResult(true, data, null));
    }

    static string Get(JsonElement req, string prop) =>
        req.TryGetProperty(prop, out var v) ? v.GetString() ?? "" : "";

    /// <summary>One stable seeded row (mcp-win1, what the xunit suite asserts
    /// on) PLUS one row per VM folder on disk carrying a meta.json — mirroring
    /// how the real 'list' reflects created guests.</summary>
    static JsonElement[] ListRows(JsonElement req)
    {
        var rows = new List<JsonElement>
        {
            Parse("""
                {"name":"mcp-win1","state":"Running","heartbeat":"OkApplicationsUnknown",
                 "linux":false,"cpu_count":2,"mem_mb":4096,"uptime":"0 00:05:00",
                 "ips":["192.168.50.31"],"os_reported":"Server 2025"}
                """),
        };
        var vmroot = Get(req, "vmroot");
        if (vmroot.Length > 0 && Directory.Exists(vmroot))
        {
            foreach (var dir in Directory.EnumerateDirectories(vmroot).OrderBy(d => d))
            {
                var meta = Path.Combine(dir, "meta.json");
                if (!File.Exists(meta)) continue;   // only linux guests carry meta
                string ip = "";
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(meta));
                    ip = doc.RootElement.TryGetProperty("ip", out var e) ? e.GetString() ?? "" : "";
                }
                catch { }
                rows.Add(JsonSerializer.SerializeToElement(new
                {
                    name = Path.GetFileName(dir),
                    state = "Running",
                    heartbeat = "Unknown",          // linux: no Hyper-V ICs
                    linux = true,
                    cpu_count = 2,
                    mem_mb = 2048,
                    uptime = "0 00:01:00",
                    ips = new[] { ip },
                    ip,
                    os_reported = "",
                }));
            }
        }
        return rows.ToArray();
    }

    /// <summary>Like the real delete: folder goes away so the next list drops
    /// the row (smoke.py polls exactly that).</summary>
    static JsonElement DeleteVm(JsonElement req)
    {
        var root = Get(req, "root");
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        var oobe = Get(req, "oobe");
        try { if (File.Exists(oobe)) File.Delete(oobe); } catch { }
        return JsonSerializer.SerializeToElement(new { deleted = true, folder = root });
    }

    static JsonElement Purge(JsonElement req)
    {
        var vmroot = Get(req, "vmroot");
        var keep = new HashSet<string>(
            req.TryGetProperty("keep", out var k)
                ? k.EnumerateArray().Select(x => x.GetString() ?? "")
                : [], StringComparer.OrdinalIgnoreCase);
        var n = 0;
        if (vmroot.Length > 0 && Directory.Exists(vmroot))
            foreach (var dir in Directory.EnumerateDirectories(vmroot))
            {
                if (keep.Contains(Path.GetFileName(dir))) continue;
                try { Directory.Delete(dir, recursive: true); n++; } catch { }
            }
        return JsonSerializer.SerializeToElement(new { deleted = n, kept = keep.Count });
    }

    /// <summary>Mirror the guest: echo the command back like PS Direct would;
    /// 'false' exits 1 like bash does.</summary>
    static JsonElement ExecResult(JsonElement req)
    {
        var cmd = req.TryGetProperty("script_b64", out var b) ? b.GetString() ?? "" : "";
        try { cmd = Encoding.UTF8.GetString(Convert.FromBase64String(cmd)); } catch { }
        // readiness probe answers with a timestamp, everything else echoes
        var outp = cmd.Contains("ready.txt") ? "ready 2026-01-01T00:00:00Z" : "exec:" + cmd;
        var exit = cmd.Trim() == "false" ? 1 : 0;
        return Parse($$"""{"output":"{{outp}}","stderr":null,"exit":{{exit}},"timed_out":false}""");
    }

    /// <summary>Like the real linux_create: write meta.json beside the VM so
    /// IsLinuxVm/LinuxIp read it back on the next call (the engine's own
    /// restart-fallback path stays honest under the fake).</summary>
    static JsonElement LinuxCreate(JsonElement req)
    {
        var root = req.GetProperty("root").GetString()!;
        Directory.CreateDirectory(root);
        var ip = req.GetProperty("ip").GetString() ?? "";
        File.WriteAllText(Path.Combine(root, "meta.json"),
            JsonSerializer.Serialize(new { linux = true, ip }));
        var name = req.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
        // SerializeToElement (not string interpolation): folder paths carry backslashes.
        return JsonSerializer.SerializeToElement(new { name, state = "Running", ip, mac = "00155d0a0b0c", folder = root });
    }

    static JsonElement SshResult(JsonElement req)
    {
        var mode = req.TryGetProperty("mode", out var m) ? m.GetString() : "exec";
        if (mode == "key")
        {
            // real linux_ssh mode=key creates the host keypair; mirror it so
            // engine.LinuxTermTargetAsync's File.Exists check passes.
            var dir = req.TryGetProperty("root", out var rd) ? rd.GetString() ?? "" : "";
            if (dir.Length > 0)
            {
                Directory.CreateDirectory(dir);
                var key = Path.Combine(dir, "linux_id_ed25519");
                if (!File.Exists(key))
                {
                    File.WriteAllText(key, "-----FAKE-KEY-----\n");
                    File.WriteAllText(key + ".pub", "ssh-ed25519 AAAAFACE fake\n");
                }
            }
            return Parse("""{"pub":"ssh-ed25519 AAAAFACE fake"}""");
        }
        var cmd = req.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
        // readiness marker probe answers; note real ssh filters the host-key
        // warning line — so does the engine, and tests assert that pairing.
        if (cmd.Contains("sandbox-ready"))
            return Parse("""{"output":"/var/lib/sandbox-ready","stderr":null,"exit":0,"timed_out":false}""");
        // bash-lite honesty: 'false' exits 1, everything else echoes
        if (cmd.Trim() == "false")
            return Parse("""{"output":"","stderr":null,"exit":1,"timed_out":false}""");
        return Parse($$"""{"output":"exec:{{cmd}}","stderr":"Warning: Permanently added 'host' (ED25519) to known_hosts\n","exit":0,"timed_out":false}""");
    }

    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
