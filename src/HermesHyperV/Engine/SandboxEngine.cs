using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HermesHyperV.Engine;

public sealed record SandboxSettings(string Root = @"C:\HyperV",
                                     string Switch = "SandboxLAN",
                                     string Tag = "hermes-mcp-sandbox");

/// <summary>
/// The whole product surface, minus HTTP: provisioning, VM lifecycle, guest
/// exec, host status. Every Hyper-V touch goes through a hidden powershell.exe
/// child running an embedded script (PowerShellRunner) — the service itself
/// is LocalSystem so there is no elevation inbox and zero visible windows.
/// </summary>
public sealed class SandboxEngine(PowerShellRunner ps, SandboxSettings cfg, ILogger<SandboxEngine> log)
{
    public string VmDir => Path.Combine(cfg.Root, "VMs");
    public string MediaDir => Path.Combine(cfg.Root, "Media");

    static readonly Regex NameRe = new(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,30}$", RegexOptions.Compiled);

    /// <summary>Validate + apply the mcp- prefix (same rules as python).</summary>
    public static string CheckName(string name)
    {
        name = name.Trim();
        if (!NameRe.IsMatch(name))
            throw new ArgumentException($"invalid VM name '{name}': use ^[A-Za-z0-9][A-Za-z0-9_-]{{0,30}}$");
        return name.StartsWith("mcp-") ? name : "mcp-" + name;
    }

    static string VmRef(string name) => name.StartsWith("mcp-") ? name[4..] : name;

    string FindIso(string iso)
    {
        if (Path.IsPathRooted(iso))
        {
            if (!File.Exists(iso)) throw new FileNotFoundException($"ISO not found: {iso}");
            return iso;
        }
        var inMedia = Path.Combine(MediaDir, iso);
        if (File.Exists(inMedia)) return inMedia;
        if (IsoCatalog.Entries.TryGetValue(iso, out var entry))
        {
            var cat = Path.Combine(MediaDir, entry.File);
            if (File.Exists(cat)) return cat;
            throw new FileNotFoundException($"catalog ISO '{entry.File}' is not in {MediaDir}");
        }
        // fuzzy: unique suffix match inside Media
        var cand = Directory.Exists(MediaDir)
            ? Directory.GetFiles(MediaDir, "*.iso").Where(f => Path.GetFileName(f).Contains(iso, StringComparison.OrdinalIgnoreCase)).ToArray()
            : [];
        if (cand.Length == 1) return cand[0];
        if (cand.Length > 1) throw new ArgumentException($"'{iso}' matches multiple ISOs: {string.Join(", ", cand.Select(Path.GetFileName))}");
        throw new FileNotFoundException($"ISO '{iso}' not found in {MediaDir} and not in catalog");
    }

    string RenderOobe(string vmName, string password)
    {
        Directory.CreateDirectory(MediaDir);
        var computer = new string((VmRef(vmName)).Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
        computer = computer.Length > 15 ? computer[..15] : computer;
        if (computer.Length == 0) computer = "SANDBOX";
        var doc = Oobe.Template
            .Replace("__COMPUTERNAME__", computer.ToUpperInvariant())
            .Replace("__PASSWORD__", System.Net.WebUtility.HtmlEncode(password));
        // HtmlEncode covers & < > which is what we need for XML text nodes
        var outPath = Path.Combine(MediaDir, $"{vmName}-oobe.xml");
        File.WriteAllText(outPath, doc, new UTF8Encoding(false));
        return outPath;
    }

    // ------------------------------------------------------------------ tools

    /// <summary>status emits one object; unwrap it and add UI fields.</summary>
    public async Task<Dictionary<string, object?>> HostStatusAsync(CancellationToken ct = default)
    {
        var r = await ps.RunScriptAsync("vm_lifecycle", new
        {
            action = "status", root = VmDir, media = MediaDir,
        }, 120, ct);
        var el = r.Require();
        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(el.GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var svc = dict.GetValueOrDefault("vmms_service")?.ToString() ?? "unknown";
        dict["vmms_running"] = svc == "running";
        dict["needs_reboot"] = false; // service IS system; hypervisor_present says all
        dict["elevation_task"] = "present"; // no task needed — service is LocalSystem
        return dict;
    }

    public async Task<JsonElement[]> ListVmsAsync(CancellationToken ct = default)
    {
        var r = await ps.RunScriptAsync("vm_lifecycle", new { action = "list", tag = cfg.Tag }, 120, ct);
        if (!r.Ok) throw new InvalidOperationException(r.Error ?? "list failed");
        return r.Data ?? [];
    }

    public static string Phase(JsonElement vm)
    {
        var state = vm.TryGetProperty("state", out var s) ? s.GetString() : null;
        if (state != "Running") return "Off";
        var hb = vm.TryGetProperty("heartbeat", out var h) ? h.GetString() ?? "" : "";
        return hb.StartsWith("Ok", StringComparison.OrdinalIgnoreCase) ? "Ready" : "Installing";
    }

    public async Task<JsonElement> VmStatusAsync(string name, CancellationToken ct = default)
    {
        var vms = await ListVmsAsync(ct);
        var full = CheckName(name);
        var hit = vms.FirstOrDefault(v => v.TryGetProperty("name", out var n) &&
                                          string.Equals(n.GetString(), full, StringComparison.OrdinalIgnoreCase));
        if (hit.ValueKind is JsonValueKind.Undefined)
            throw new InvalidOperationException($"VM '{full}' not found");
        return hit;
    }

    public async Task<JsonElement> ListEditionsAsync(string iso, CancellationToken ct = default)
    {
        var path = FindIso(iso);
        var r = await ps.RunScriptAsync("vm_lifecycle", new { action = "editions", iso = path }, 180, ct);
        if (!r.Ok) throw new InvalidOperationException(r.Error ?? "editions failed");
        return JsonSerializer.SerializeToElement(new { iso = path, editions = r.Data });
    }

    /// <summary>
    /// Only the golden build needs serialization (concurrent builds of the
    /// same key would fight over its seed dirs). Child-disk creation uses
    /// per-VM mount-point folders and runs fully concurrently.
    /// </summary>
    static readonly SemaphoreSlim GoldenGate = new(1, 1);

    /// <summary>
    /// Golden images are keyed by (ISO file, edition, disk size): build once,
    /// reuse forever. Differencing children reference it, so it must not move.
    /// </summary>
    string GoldenPath(string isoPath, string edition, int diskGb)
    {
        var key = Path.GetFileName(isoPath).ToLowerInvariant() + "|" +
                  edition.ToLowerInvariant() + "|" + diskGb;
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(MediaDir, "golden", Convert.ToHexString(hash)[..16].ToLowerInvariant() + ".vhdx");
    }

    /// <summary>
    /// Create + start a disposable Gen-2 VM from the golden image: an
    /// instant differencing child VHDX + per-VM Panther unattend (first call
    /// for an ISO/edition also builds the golden once, ~2-4 min). Returns
    /// with the VM booting into auto-OOBE; desktop answers run_ps ~1-2 min
    /// later.
    /// </summary>
    public async Task<Dictionary<string, object?>> CreateVmAsync(
        string name, string iso = "server2025-eval", string? edition = null,
        int memoryMb = 0, int maxMemoryMb = 0, int cpu = 2, int diskGb = 0,
        string? password = null, CancellationToken ct = default)
    {
        var vmName = CheckName(name);
        var path = FindIso(iso);
        IsoEntry? cat = null;
        if (edition is null)
        {
            if (IsoCatalog.Entries.TryGetValue(iso, out var entry) &&
                string.Equals(Path.GetFileName(path), entry.File, StringComparison.OrdinalIgnoreCase))
            { edition = entry.Edition; cat = entry; }
            else
                throw new ArgumentException("edition unknown for this ISO; call list_editions first");
        }
        // per-SKU sizing defaults (Win11 wants 8 GB, Server 4 GB) unless overridden
        if (memoryMb == 0) memoryMb = cat?.MemoryMb ?? 4096;
        if (maxMemoryMb == 0) maxMemoryMb = Math.Min(memoryMb * 2, 16384);
        if (diskGb == 0) diskGb = cat?.DiskGb ?? 64;
        memoryMb = Math.Clamp(memoryMb, 1024, 65536);
        maxMemoryMb = Math.Clamp(maxMemoryMb, memoryMb, 65536);
        cpu = Math.Clamp(cpu, 1, 128);
        diskGb = Math.Clamp(diskGb, 40, 2040);
        password ??= GuestDefaults.Password;

        var oobe = RenderOobe(vmName, password);
        var vroot = Path.Combine(VmDir, vmName);

        var golden = GoldenPath(path, edition, diskGb);
        if (!File.Exists(golden))
        {
            // One build per (ISO, edition, disk size), serialized; every
            // later create skips this entirely.
            await GoldenGate.WaitAsync(ct);
            try
            {
                if (!File.Exists(golden))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(golden)!);
                    log.LogInformation("building golden image {Golden} from {Iso} (once per ISO+edition, ~2-4 min)",
                        golden, Path.GetFileName(path));
                    var gold = await ps.RunScriptAsync("golden_image", new
                    {
                        golden, iso = path, edition, disk_gb = diskGb,
                    }, 2400, ct);
                    if (!gold.Ok) throw new InvalidOperationException($"golden image build failed: {gold.Error}");
                }
            }
            finally { GoldenGate.Release(); }
        }

        try
        {
            // child vhdx + per-VM Panther unattend (~5-10 s, concurrent-safe)
            log.LogInformation("spawning {Vm} from golden ({Golden})", vmName, Path.GetFileName(golden));
            var child = await ps.RunScriptAsync("child_disk", new
            {
                golden, root = vroot, unattend = oobe,
            }, 300, ct);
            if (!child.Ok) throw new InvalidOperationException($"child disk creation failed: {child.Error}");

            var create = await ps.RunScriptAsync("vm_lifecycle", new
            {
                action = "create", name = vmName, root = vroot,
                mem_mb = memoryMb, max_mb = maxMemoryMb, cpu,
                @switch = cfg.Switch, tag = cfg.Tag,
            }, 300, ct);
            var created = create.Require();
            log.LogInformation("VM {Vm} created, state={State}", vmName,
                created.TryGetProperty("state", out var st) ? st.GetString() : "?");

            return new Dictionary<string, object?>
            {
                ["vm"] = vmName,
                ["state"] = created.TryGetProperty("state", out var s2) ? s2.GetString() : null,
                ["folder"] = vroot,
                ["guest_user"] = GuestDefaults.User,
                ["guest_password"] = password,
                ["iso"] = path,
                ["edition"] = edition,
                ["note"] = "Windows pre-applied; boots into auto-OOBE. Poll vm_status — when " +
                           "heartbeat is Ok / run_ps answers, the guest is live (~2-4 min).",
            };
        }
        finally { /* per-VM seed dirs are cleaned by the script itself */ }
    }

    public Task<JsonElement> PowerVmAsync(string name, string want, CancellationToken ct = default)
    {
        var full = CheckName(name);
        return Run1("vm_lifecycle", new { action = "power", name = full, want }, 300, ct);
    }

    public Task<JsonElement> StartVmAsync(string name, CancellationToken ct = default) =>
        Run1("vm_lifecycle", new { action = "power", name = CheckName(name), want = "start" }, 300, ct);

    public Task<JsonElement> StopVmAsync(string name, CancellationToken ct = default) =>
        Run1("vm_lifecycle", new { action = "power", name = CheckName(name), want = "stop" }, 300, ct);

    public Task<JsonElement> DeleteVmAsync(string name, CancellationToken ct = default)
    {
        var full = CheckName(name);
        return Run1("vm_lifecycle", new
        {
            action = "delete", name = full, tag = cfg.Tag,
            root = Path.Combine(VmDir, full),
            oobe = Path.Combine(MediaDir, $"{full}-oobe.xml"),
        }, 300, ct);
    }

    public Task<JsonElement> PurgeAsync(CancellationToken ct = default) =>
        Run1("vm_lifecycle", new { action = "purge", tag = cfg.Tag, vmroot = VmDir, media = MediaDir }, 600, ct);

    /// <summary>PowerShell Direct into the guest (VMBus — works without network).</summary>
    public async Task<JsonElement> RunPsAsync(string name, string command, int timeoutSec = 60,
                                              string? user = null, string? password = null, CancellationToken ct = default)
    {
        var full = CheckName(name);
        var cmdB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));
        var r = await ps.RunScriptAsync("guest_exec", new
        {
            vm = full, script_b64 = cmdB64,
            user = user ?? GuestDefaults.User,
            password = password ?? GuestDefaults.Password,
            timeout = timeoutSec,
        }, timeoutSec + 90, ct);
        if (!r.Ok) throw new InvalidOperationException(r.Error ?? "guest exec failed");
        var first = r.First ?? throw new InvalidOperationException("guest exec emitted nothing");
        var wrapped = first.TryGetProperty("output", out var o) ? o.GetString() : null;
        var timedOut = first.TryGetProperty("timed_out", out var t) && t.GetBoolean();
        return JsonSerializer.SerializeToElement(new
        {
            vm = full,
            output = wrapped,
            timed_out = timedOut,
        });
    }

    async Task<JsonElement> Run1(string script, object req, int timeout, CancellationToken ct)
    {
        var r = await ps.RunScriptAsync(script, req, timeout, ct);
        return r.Require();
    }

    /// <summary>Wait for heartbeat Ok (guest OS up). Cheap polling in one ps child.</summary>
    public async Task<bool> WaitReadyAsync(string name, int timeoutSec = 900, CancellationToken ct = default)
    {
        var full = CheckName(name);
        while (timeoutSec > 0)
        {
            var r = await ps.RunScriptAsync("vm_lifecycle", new { action = "wait", name = full, want = "Running", timeout_s = Math.Min(60, timeoutSec) }, 120, ct);
            if (r.Ok)
            {
                try
                {
                    var st = await VmStatusAsync(full, ct);
                    if (Phase(st) == "Ready") return true;
                }
                catch { }
            }
            timeoutSec -= 60;
        }
        return false;
    }
}
