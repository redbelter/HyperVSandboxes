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
    /// S/W/R drive letters are a machine-global namespace, and provisioning
    /// binds them to ITS OWN partitions. Two concurrent create_vm jobs would
    /// collide on them (or yank W: out from under each other's dism apply),
    /// so provisioning is a global critical section: concurrent calls queue.
    /// </summary>
    static readonly SemaphoreSlim ProvisionGate = new(1, 1);

    /// <summary>
    /// Provision + create + start a disposable Gen-2 VM. Blocking: image
    /// apply takes ~8-12 min (runs in one hidden dism.exe). Returns once the
    /// VM is Running; guest answers run_ps ~2-4 min later (heartbeat Ok).
    /// </summary>
    public async Task<Dictionary<string, object?>> CreateVmAsync(
        string name, string iso = "server2025-eval", string? edition = null,
        int memoryMb = 4096, int maxMemoryMb = 8196, int cpu = 2, int diskGb = 64,
        string? password = null, CancellationToken ct = default)
    {
        var vmName = CheckName(name);
        var path = FindIso(iso);
        if (edition is null)
        {
            if (IsoCatalog.Entries.TryGetValue(iso, out var entry) &&
                string.Equals(Path.GetFileName(path), entry.File, StringComparison.OrdinalIgnoreCase))
                edition = entry.Edition;
            else
                throw new ArgumentException("edition unknown for this ISO; call list_editions first");
        }
        memoryMb = Math.Clamp(memoryMb, 1024, 65536);
        maxMemoryMb = Math.Clamp(maxMemoryMb, memoryMb, 65536);
        cpu = Math.Clamp(cpu, 1, 128);
        diskGb = Math.Clamp(diskGb, 40, 2040);
        password ??= GuestDefaults.Password;

        var oobe = RenderOobe(vmName, password);
        var vroot = Path.Combine(VmDir, vmName);

        await ProvisionGate.WaitAsync(ct);
        try
        {
            log.LogInformation("provisioning {Vm} from {Iso} (~10 min, hidden dism)", vmName, Path.GetFileName(path));
            var prov = await ps.RunScriptAsync("provision_disk", new
            {
                root = vroot, vmroot = VmDir, iso = path, edition, unattend = oobe, disk_gb = diskGb,
            }, 1800, ct);
            if (!prov.Ok) throw new InvalidOperationException($"provisioning failed: {prov.Error}");

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
        finally { ProvisionGate.Release(); }
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
