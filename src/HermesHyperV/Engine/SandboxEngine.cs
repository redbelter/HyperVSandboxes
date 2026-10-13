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

    /// <summary>Records the guest creds beside the VM (creds.json). Delete wipes
    /// the whole folder, so nothing lingers. Dashboard surface only — the API
    /// binds loopback; anyone with dashboard access can already run_ps as root.</summary>
    void WriteCreds(string vmName, string user, string password, string? ip = null)
    {
        try
        {
            var dir = Path.Combine(VmDir, vmName);
            Directory.CreateDirectory(dir);
            var o = new Dictionary<string, object?> { ["user"] = user, ["password"] = password };
            if (ip != null) o["ip"] = ip;
            File.WriteAllText(Path.Combine(dir, "creds.json"),
                JsonSerializer.Serialize(o));
        }
        catch { /* cosmetic; create must not fail over it */ }
    }
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

    /// <summary>Locate the catalog raw image for a Linux SKU.</summary>
    string FindRaw(string raw)
    {
        var inMedia = Path.Combine(MediaDir, raw);
        if (File.Exists(inMedia)) return inMedia;
        throw new FileNotFoundException($"raw image '{raw}' is not in {MediaDir}");
    }

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
        var r = await ps.RunScriptAsync("vm_lifecycle", new { action = "list", tag = cfg.Tag, vmroot = VmDir }, 120, ct);
        if (!r.Ok) throw new InvalidOperationException(r.Error ?? "list failed");
        // stamp the honest phase (desktop-confirmed) onto every VM row
        return (r.Data ?? []).Select(vm =>
        {
            var o = JsonSerializer.Deserialize<Dictionary<string, object?>>(vm.GetRawText())!;
            o["phase"] = Phase(vm);
            var nm = vm.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            o["stuck"] = StuckOf(nm);
            var credFile = Path.Combine(VmDir, nm, "creds.json");
            if (nm.Length > 0 && File.Exists(credFile))
            {
                try
                {
                    using var cd = JsonDocument.Parse(File.ReadAllText(credFile));
                    if (cd.RootElement.TryGetProperty("user", out var cu)) o["guest_user"] = cu.GetString();
                    if (cd.RootElement.TryGetProperty("password", out var cp)) o["guest_password"] = cp.GetString();
                }
                catch { }
            }
            return JsonSerializer.SerializeToElement(o);
        }).ToArray();
    }

    public static string Phase(JsonElement vm)
    {
        var state = vm.TryGetProperty("state", out var s) ? s.GetString() : null;
        if (state != "Running") return "Off";
        var linux = vm.TryGetProperty("linux", out var lz) && lz.GetBoolean();
        var hb = vm.TryGetProperty("heartbeat", out var h) ? h.GetString() ?? "" : "";
        var name = vm.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        // Linux guests have no Hyper-V IC, so heartbeat never leaves Unknown;
        // their readiness signal alone is the ssh marker (ReadyCache).
        if (!linux && !hb.StartsWith("Ok", StringComparison.OrdinalIgnoreCase))
        {
            // Heartbeat dark while Hyper-V insists it's Running. A guest that
            // bugchecks into WinRE keeps its worker process alive (uptime
            // climbs, state stays Running) while the OS never answers — from
            // the host that is indistinguishable from a slow install UNLESS we
            // watch. Watch knows: OS-reported-in-once + dark = it crashed out
            // (likely WinRE/BSOD loop); never-reported + dark past budget =
            // stuck pre-OS (boot screen, WinRE, corrupt disk).
            return Watch.Verdict(name) != null ? "Stuck" : "Installing";
        }
        // Heartbeat Ok only means the kernel/ICs respond. Honest 'Ready' =
        // desktop actually logged on (ready.txt seen by the readiness prober).
        return ReadyCache.ContainsKey(name) ? "Ready" : "Installing";
    }

    /// <summary>Full human sentence behind a 'Stuck' phase (null when healthy/inside budget).</summary>
    public static string? StuckOf(string name) => Watch.Verdict(name);

    /// <summary>
    /// Host-side health verdict for a VM: the list-row signals (heartbeat,
    /// KVP OS report, uptime) plus recent Hyper-V worker/VMMS events. Cheap
    /// enough for the dashboard to poll; the honest answer to "is this guest
    /// actually alive, or is it bugchecking in a circle?".
    /// </summary>
    public async Task<JsonElement> VmHealthAsync(string name, CancellationToken ct = default)
    {
        var vm = await VmStatusAsync(name, ct);
        var nm = vm.GetProperty("name").GetString()!;
        var ev = await ps.RunScriptAsync("vm_lifecycle",
            new { action = "diag", name = nm, minutes = 60 }, 60, ct);
        var events = ev.Ok && ev.First is { } f && f.TryGetProperty("events", out var e)
            ? e.EnumerateArray().Take(10).ToArray() : Array.Empty<JsonElement>();
        Watch.Touch(nm, vm);
        return JsonSerializer.SerializeToElement(new
        {
            vm = nm,
            state = vm.TryGetProperty("state", out var s) ? s.GetString() : null,
            heartbeat = vm.TryGetProperty("heartbeat", out var h) ? h.GetString() : null,
            phase = Phase(vm),
            diagnosis = Watch.Verdict(nm) ?? Watch.LastDiagnosis(nm),
            os_reported = Watch.OsReported(nm),
            uptime = vm.TryGetProperty("uptime", out var u) ? u.GetString() : null,
            linux = vm.TryGetProperty("linux", out var lz) && lz.GetBoolean(),
            recent_events = events,
        });
    }

    /// <summary>
    /// Tracks what the HOST can see per VM (heartbeat transitions, KVP "the
    /// guest OS introduced itself" cache — which outlives this service).
    /// Produces honest verdicts instead of an eternal 'Installing' when the
    /// guest is really stuck at a pre-desktop screen (WinRE, BSOD, boot
    /// prompt). Pure inference from signals Hyper-V exposes; no guest access
    /// needed, so it works while run_ps is dead.
    /// </summary>
    internal static class Watch
    {
        public const int BootBudgetMin = 15;   // never-OS-reported past this = stuck pre-OS
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> EverOk = new();
        // Guest-shell (PS Direct) liveness: last success and last transport-level
        // death ("socket target process has ended" = VMGS/PowerShell gone while
        // the worker still runs — the WinRE / WinPE / BSOD-screen signature).
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> ExecOkAt = new();
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> ExecBadAt = new();

        public static void MarkExecOk(string name) => ExecOkAt[name] = DateTime.UtcNow;
        public static void MarkExecBad(string name) => ExecBadAt[name] = DateTime.UtcNow;
        public static void ClearAll()
        {
            EverOk.Clear(); ExecOkAt.Clear(); ExecBadAt.Clear();
            LastRaw.Clear(); LastSeenDark.Clear(); LastSeenDiag.Clear(); DarkSince.Clear();
        }
        public static void Forget(string name)
        {
            EverOk.TryRemove(name, out _); ExecOkAt.TryRemove(name, out _); ExecBadAt.TryRemove(name, out _);
            LastRaw.TryRemove(name, out _); LastSeenDark.TryRemove(name, out _);
            LastSeenDiag.TryRemove(name, out _); DarkSince.TryRemove(name, out _);
        }

        public static void Touch(string name, JsonElement vm)
        {
            var hb = vm.TryGetProperty("heartbeat", out var h) ? h.GetString() ?? "" : "";
            if (hb.StartsWith("Ok", StringComparison.OrdinalIgnoreCase))
                EverOk.TryAdd(name, DateTime.UtcNow);
        }

        /// <summary>
        /// Called for every Running VM row every poll: maintains dark-since
        /// timers, caches the raw signals Verdict() reasons over, and blogs
        /// health transitions once (dark->stuck, dark->recovered) so the
        /// activity feed tells the human before they have to ask.
        /// </summary>
        public static void Observe(string name, JsonElement vm, JobStore jobs)
        {
            var hb = vm.TryGetProperty("heartbeat", out var h) ? h.GetString() ?? "" : "";
            var st = vm.TryGetProperty("state", out var s) ? s.GetString() : null;
            if (st != "Running") { LastRaw.TryRemove(name, out _); DarkSince.TryRemove(name, out _); return; }
            var linux = vm.TryGetProperty("linux", out var lz) && lz.GetBoolean();
            double upMin = 0;
            if (vm.TryGetProperty("uptime", out var u) &&
                TimeSpan.TryParse(u.GetString(), out var up)) upMin = up.TotalMinutes;
            var reported = vm.TryGetProperty("os_reported", out var or) &&
                           !string.IsNullOrEmpty(or.GetString());
            var darkOk = hb.StartsWith("Ok", StringComparison.OrdinalIgnoreCase);
            var dark = TimeSpan.Zero;
            if (linux)
            {
                // No heartbeat IC on Linux guests — no darkness concept.
            }
            else if (!darkOk)
            {
                var now = DateTime.UtcNow;
                var since = DarkSince.GetOrAdd(name, now);
                dark = now - since;
            }
            else
            {
                DarkSince.TryRemove(name, out _);   // only a live heartbeat ends darkness
                darkOk = true;
            }
            LastRaw[name] = (linux, upMin, reported, dark);
            if (darkOk)
            {
                Touch(name, vm);
                var prev = Watch.LastSeenDiag.GetOrAdd(name, "healthy");
                if (prev != "healthy")
                {
                    LastSeenDiag[name] = "healthy";
                    jobs.Blog($"{name}: heartbeat recovered", "ready");
                }
                return;
            }
            // shell gone = guest fell out of a usable OS; revoke stale readiness
            if (!linux && Watch.ShellGone(name))
            {
                if (ReadyCache.TryRemove(name, out _))
                    jobs.Blog($"{name}: readiness revoked — guest shell unreachable (WinRE?)", "warn");
            }
            var verdict = Verdict(name);
            if (verdict != null)
            {
                LastSeenDark[name] = verdict;
                if (Watch.LastSeenDiag.GetOrAdd(name, "") != verdict)
                {
                    LastSeenDiag[name] = verdict;
                    jobs.Blog($"{name}: {verdict}", "warn");
                }
            }
        }

        /// <summary>Verdict while the VM looks unhealthy; null = healthy/still-booting.</summary>
        public static string? Verdict(string name)
        {
            if (!LastRaw.TryGetValue(name, out var raw)) return null;
            var (linux, upMin, reported, dark) = raw;
            if (linux) return null;
            var kernelAlive = dark <= TimeSpan.FromSeconds(90);   // heartbeat flicker grace
            if (kernelAlive)
            {
                // Heartbeat beats (kernel ICs answer — they do in WinRE too), but the
                // guest SHELL (VMGS/PowerShell, what run_ps rides) has been dead for
                // minutes after having worked once. That split is WinRE/WinPE/BSOD-screen.
                if (ShellGone(name))
                    return "Guest kernel answers but the guest shell is unreachable (PS Direct socket dead) — classic WinRE / WinPE / BSOD-screen state";
                return null;   // healthy or still inside boot budget
            }
            if (EverOk.ContainsKey(name))
                return "Lost contact — the guest OS booted, then went silent (bugcheck / WinRE / crash loop)";
            if (reported)
                return "OS reported in once, now silent — likely crashed to WinRE";
            if (upMin >= BootBudgetMin)
                return $"OS never reported in {upMin:N0} min (boot budget {BootBudgetMin}) — stuck at boot screen / WinRE / corrupt boot disk";
            return null;   // still inside the boot budget — 'Installing' is honest
        }

        /// <summary>Shell dead >2 min. Two evidence paths: (a) exec worked once
        /// and now transport-dies — unambiguous; (b) service restarted (no exec
        /// history here), so fall back to the host-cached KVP report + boot
        /// budget: the OS introduced itself long ago yet its shell has never
        /// answered here → it isn't sitting at a shell at all.</summary>
        public static bool ShellGone(string name)
        {
            var ok = ExecOkAt.TryGetValue(name, out var o) ? o : DateTime.MinValue;
            var bad = ExecBadAt.TryGetValue(name, out var b) ? b : DateTime.MinValue;
            if (bad == DateTime.MinValue || DateTime.UtcNow - bad <= TimeSpan.FromMinutes(2)) return false;
            if (ok > DateTime.MinValue) return true;
            if (LastRaw.TryGetValue(name, out var raw) && raw.reported && raw.upMin >= BootBudgetMin)
                return true;
            return false;
        }

        public static string LastDiagnosis(string name) =>
            LastSeenDark.TryGetValue(name, out var v) ? v : "healthy";

        public static bool OsReported(string name) =>
            LastRaw.TryGetValue(name, out var raw) && raw.reported;

        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> LastSeenDiag = new();

        // (linux, uptimeMinutes, osEverReportedToHost, darkSince — zero if heartbeat currently Ok)
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool linux, double upMin, bool reported, TimeSpan dark)> LastRaw = new();
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> LastSeenDark = new();
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> DarkSince = new();
    }

    /// <summary>
    /// Polls ready.txt for every heartbeat-Ok VM, so list_vms/dashboard/
    /// wait_ready share one honest readiness view without callers probing.
    /// Blogs the transition so the activity feed says when a VM is truly usable.
    /// </summary>
    internal sealed class ReadinessProber(SandboxEngine engine, JobStore jobs,
        ILogger<ReadinessProber> log) : BackgroundService
    {
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> KeepAliveAt = new();
        const string Probe =
            "if (Test-Path C:\\Sandbox\\ready.txt) { Get-Content C:\\Sandbox\\ready.txt } " +
            "else { 'NOT-YET' }";

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    foreach (var vm in await engine.ListVmsAsync(ct))
                    {
                        var name = vm.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var hb = vm.TryGetProperty("heartbeat", out var h) ? h.GetString() ?? "" : "";
                        var st = vm.TryGetProperty("state", out var s2) ? s2.GetString() : null;
                        var linux = vm.TryGetProperty("linux", out var lz) && lz.GetBoolean();
                        if (st != "Running") continue;
                        // host-side health watch runs for EVERY running row
                        // (heartbeat transitions, WinRE/BSOD-loop verdicts)
                        Watch.Observe(name, vm, jobs);
                        if (!linux && !hb.StartsWith("Ok", StringComparison.OrdinalIgnoreCase)) continue;
                        // Ready VMs still need a liveness pulse: a guest that
                        // silently rebooted into WinRE looks identical from the
                        // host (heartbeat Ok, uptime monotonic) until someone
                        // touches the exec channel. Throttle to ~every 30s/VM.
                        if (ReadyCache.ContainsKey(name))
                        {
                            var last = KeepAliveAt.TryGetValue(name, out var l) ? l : DateTime.MinValue;
                            if (DateTime.UtcNow - last < TimeSpan.FromSeconds(30)) continue;
                            KeepAliveAt[name] = DateTime.UtcNow;
                            try
                            {
                                if (linux)
                                    await engine.RunLinuxSshAsync(name, "cat /var/lib/sandbox-ready", 30, ct);
                                else
                                    await engine.RunPsAsync(name, Probe, 30, ct: ct);
                            }
                            catch { /* failure is enough — NoteExec already marked it */ }
                            continue;
                        }
                        try
                        {
                            var r = linux
                                ? await engine.RunLinuxSshAsync(name, "cat /var/lib/sandbox-ready 2>/dev/null || echo NOT-YET", 30, ct)
                                : await engine.RunPsAsync(name, Probe, 30, ct: ct);
                            var outp = (r.TryGetProperty("output", out var o) ? o.GetString() : "")?.Trim() ?? "";
                            if (outp.Length > 0 && !outp.Contains("NOT-YET"))
                            {
                                ReadyCache[name] = outp;
                                jobs.Blog($"{name}: desktop ready ({outp})", "ready");
                            }
                        }
                        catch { /* guest not answering yet — expected mid-boot */ }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { log.LogDebug(ex, "readiness probe cycle failed"); }
                try { await Task.Delay(8000, ct); } catch { break; }
            }
        }
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
    /// Desktop-readiness markers observed (vm -> ready.txt content). Sticky
    /// per VM lifetime: once the desktop logged on, it stays usable.
    /// </summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ReadyCache = new();

    /// <summary>
    /// Block until the guest DESKTOP is actually up: polls the FirstLogonCommands
    /// marker C:\Sandbox\ready.txt over PS Direct. Heartbeat Ok only proves the
    /// kernel/ICs respond — PS Direct works during an OOBE stall, so this
    /// marker is the only honest "usable" signal.
    /// </summary>
    public async Task<Dictionary<string, object?>> WaitReadyAsync(
        string name, int timeoutSec = 300, CancellationToken ct = default)
    {
        var full = CheckName(name);
        timeoutSec = Math.Clamp(timeoutSec, 10, 3600);
        if (ReadyCache.TryGetValue(full, out var hot))
            return new Dictionary<string, object?>
            {
                ["vm"] = full, ["ready"] = true, ["ready_at"] = hot, ["cached"] = true,
            };

        var isLinux = IsLinuxVm(full);
        const string probe =
            "if (Test-Path C:\\Sandbox\\ready.txt) { Get-Content C:\\Sandbox\\ready.txt } " +
            "else { 'NOT-YET' }";
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        string? lastState = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var r = isLinux
                    ? await RunLinuxSshAsync(full, "cat /var/lib/sandbox-ready 2>/dev/null || echo NOT-YET", 40, ct)
                    : await RunPsAsync(full, probe, 40, ct: ct);
                var outp = (r.TryGetProperty("output", out var o) ? o.GetString() : "")?.Trim() ?? "";
                lastState = outp;
                if (outp.Length > 0 && !outp.Contains("NOT-YET"))
                {
                    ReadyCache[full] = outp;
                    return new Dictionary<string, object?>
                    {
                        ["vm"] = full, ["ready"] = true, ["ready_at"] = outp, ["cached"] = false,
                    };
                }
                lastState = "desktop session up, marker pending";
            }
            catch (Exception ex)
            {
                // PS Direct refuses until integration components are up;
                // that is expected while the VM is booting — keep polling.
                lastState = ex.Message.Split('\n')[0] is var s && s.Length > 90 ? s[..90] : ex.Message;
            }
            await Task.Delay(5000, ct);
        }
        throw new TimeoutException(
            $"guest desktop not ready within {timeoutSec}s (last: {lastState}). " +
            "A VM stuck here past ~5 min usually means OOBE stalled — check Panther logs.");
    }

    /// <summary>Golden images are keyed by (ISO file, edition, disk size): build once,
    /// reuse forever. Differencing children reference it, so it must not move.
    /// </summary>
    string GoldenPath(string isoPath, string edition, int diskGb, string ext = ".vhdx")
    {
        var key = Path.GetFileName(isoPath).ToLowerInvariant() + "|" +
                  edition.ToLowerInvariant() + "|" + diskGb;
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(MediaDir, "golden", Convert.ToHexString(hash)[..16].ToLowerInvariant() + ext);
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
        ReadyCache.TryRemove(vmName, out _);   // fresh VM = no stale readiness
        Watch.Forget(vmName);                  // ...or stale health history
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

            WriteCreds(vmName, GuestDefaults.User, password);
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

    /// <summary>
    /// Next free static IP on the internal switch (192.168.50.x). Guests get
    /// their address from the cloud-init seed, so the host always knows where
    /// to SSH without any DHCP service. Released on delete/purge.
    /// </summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> LinuxIpPool = new();

    string ClaimIp(string vmName) => LinuxIpPool.GetOrAdd(vmName,
        _ => "192.168.50." + (10 + LinuxIpPool.Count % 200));

    /// <summary>IP from pool, falling back to the VM's meta.json (service
    /// restarts lose the pool; meta.json was written by linux_create).</summary>
    string? LinuxIp(string vmName)
    {
        if (LinuxIpPool.TryGetValue(vmName, out var ip)) return ip;
        var meta = Path.Combine(VmDir, vmName, "meta.json");
        if (File.Exists(meta))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(meta));
                if (doc.RootElement.TryGetProperty("ip", out var e))
                {
                    ip = e.GetString();
                    if (!string.IsNullOrWhiteSpace(ip)) { LinuxIpPool[vmName] = ip; return ip; }
                }
            }
            catch { }
        }
        return null;
    }

    public bool IsLinuxVm(string vmName) => File.Exists(Path.Combine(VmDir, vmName, "meta.json"));

    /// <summary>Resolve what /ws/term needs: guest IP + service ssh key path
    /// (created on demand). Throws with a human message the terminal prints.</summary>
    public async Task<(string ip, string key)> LinuxTermTargetAsync(string name, CancellationToken ct)
    {
        var full = CheckName(name);
        if (!IsLinuxVm(full))
            throw new InvalidOperationException(
                $"{full} is not a Linux sandbox — Windows guests have no SSH listener; use run_ps");
        var ip = LinuxIp(full) ?? throw new InvalidOperationException(
            $"{full} has no recorded IP — start it, or the service lost the pool (recreate)");
        var pubkey = await LinuxHostKeyAsync(ct);   // ensures the keypair exists
        var key = Path.Combine(cfg.Root, "Linux", "linux_id_ed25519");
        if (!File.Exists(key)) throw new InvalidOperationException("service ssh key missing: " + key);
        _ = pubkey;
        return (ip, key);
    }

    async Task<string> LinuxHostKeyAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(cfg.Root, "Linux"));
        var r = await ps.RunScriptAsync("linux_ssh", new
        {
            mode = "key", root = Path.Combine(cfg.Root, "Linux"),
        }, 120, ct);
        var first = r.Require();
        return first.GetProperty("pub").GetString()!;
    }

    /// <summary>
    /// Debian/cloud image: golden is the converted raw (once), each VM is a
    /// differencing child + per-VM NoCloud seed (hostname, pubkey, static IP).
    /// Guest channel is SSH, not PowerShell Direct.
    /// </summary>
    public async Task<Dictionary<string, object?>> CreateLinuxVmAsync(
        string name, string iso, int memoryMb = 0, int cpu = 2, int diskGb = 0,
        string? password = null, CancellationToken ct = default)
    {
        var vmName = CheckName(name);
        ReadyCache.TryRemove(vmName, out _);
        if (!IsoCatalog.Entries.TryGetValue(iso, out var cat) || cat.Raw.Length == 0)
            throw new ArgumentException($"'{iso}' is not a linux SKU in the catalog");
        var raw = FindRaw(cat.Raw);
        if (memoryMb == 0) memoryMb = cat.MemoryMb;
        if (diskGb == 0) diskGb = cat.DiskGb;
        memoryMb = Math.Clamp(memoryMb, 512, 65536);
        cpu = Math.Clamp(cpu, 1, 128);
        diskGb = Math.Clamp(diskGb, 4, 2040);

        // fixed VHD keeps the raw size; a differencing child may be larger,
        // so the golden is shared across all disk sizes for this image.
        var golden = GoldenPath(cat.Raw, "linux", 0);
        if (!File.Exists(golden))
        {
            await GoldenGate.WaitAsync(ct);
            try
            {
                if (!File.Exists(golden))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(golden)!);
                    log.LogInformation("converting linux golden {Golden} from {Raw} (once, ~2 min)",
                        golden, Path.GetFileName(raw));
                    var gold = await ps.RunScriptAsync("linux_golden", new
                    {
                        vhd = golden, raw,
                    }, 2400, ct);
                    if (!gold.Ok) throw new InvalidOperationException($"linux golden failed: {gold.Error}");
                }
            }
            finally { GoldenGate.Release(); }
        }

        password = string.IsNullOrWhiteSpace(password) ? GuestDefaults.Password : password;
        var ip = ClaimIp(vmName);
        var pubkey = await LinuxHostKeyAsync(ct);
        var hostname = new string(VmRef(vmName).Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
        if (hostname.Length == 0) hostname = "sandbox";
        if (hostname.Length > 15) hostname = hostname[..15];

        log.LogInformation("spawning linux {Vm} from golden ({Golden}) ip={Ip}", vmName,
            Path.GetFileName(golden), ip);
        var create = await ps.RunScriptAsync("vm_lifecycle", new
        {
            action = "linux_create", name = vmName, root = Path.Combine(VmDir, vmName),
            golden, mem_mb = memoryMb, cpu, disk_gb = diskGb,
            @switch = cfg.Switch, tag = cfg.Tag,
            ip, hostname, pubkey, password,
        }, 600, ct);
        var created = create.Require();
        WriteCreds(vmName, cat.User, password, ip);

        return new Dictionary<string, object?>
        {
            ["vm"] = vmName,
            ["state"] = created.TryGetProperty("state", out var s2) ? s2.GetString() : null,
            ["ip"] = ip,
            ["guest_user"] = cat.User,
            ["guest_password"] = password,
            ["auth"] = $"ssh key (service-managed) + password; ssh {cat.User}@{ip} or console",
            ["folder"] = Path.Combine(VmDir, vmName),
            ["note"] = $"Debian boots with cloud-init; first boot ~1 min. Then run_ps '{name}' runs " +
                       "shell over SSH as " + cat.User + " (bash, NOT PowerShell). " +
                       "wait_ready confirms /var/lib/sandbox-ready.",
        };
    }

    /// <summary>SSH into a linux sandbox VM (BatchMode, service key).</summary>
    public async Task<JsonElement> RunLinuxSshAsync(string name, string command, int timeoutSec = 60,
                                                    CancellationToken ct = default)
    {
        var full = CheckName(name);
        var ip = LinuxIp(full) ?? throw new InvalidOperationException(
            $"no ip recorded for {full} — restart the service loses the pool; VM is still reachable " +
            "from its Hyper-V console");
        var r = await ps.RunScriptAsync("linux_ssh", new
        {
            mode = "exec", root = Path.Combine(cfg.Root, "Linux"),
            host = ip, cmd = command, timeout_s = timeoutSec,
        }, timeoutSec + 60, ct);
        var first = r.First ?? throw new InvalidOperationException(
            !string.IsNullOrEmpty(r.Error) ? $"ssh exec failed: {r.Error}" : "ssh exec emitted nothing");
        // data[0].step='exec' with the result nested? we emit @{step='exec'} then the table:
        // simpler contract: find a member carrying 'output'
        var node = first;
        if (!first.TryGetProperty("output", out _) &&
            first.TryGetProperty("result", out var nested)) node = nested;
        var outp = node.TryGetProperty("output", out var o) ? o.GetString() : null;
        var errp = node.TryGetProperty("stderr", out var er) ? er.GetString() : null;
        // ssh prints host-key warnings on stderr on the first connect — not an error
        if (errp != null && errp.Replace("\n", "").Replace("\r", "").Trim()
                .StartsWith("Warning: Permanently added", StringComparison.OrdinalIgnoreCase))
            errp = errp.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                       .Where(l => !l.TrimStart().StartsWith("Warning: Permanently added"))
                       .DefaultIfEmpty("").Aggregate((a, b) => a + "\n" + b);
        var timedOut = node.TryGetProperty("timed_out", out var t) && t.ValueKind == JsonValueKind.True;
        var exit = node.TryGetProperty("exit", out var ex) && ex.ValueKind == JsonValueKind.Number && ex.TryGetInt32(out var ei) ? ei : (int?)null;
        return JsonSerializer.SerializeToElement(new
        { vm = full, ip, output = outp, stderr = string.IsNullOrWhiteSpace(errp) ? null : errp,
          exit, timed_out = timedOut });
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

    public async Task<JsonElement> DeleteVmAsync(string name, CancellationToken ct = default)
    {
        var full = CheckName(name);
        var r = await Run1("vm_lifecycle", new
        {
            action = "delete", name = full, tag = cfg.Tag,
            root = Path.Combine(VmDir, full),
            oobe = Path.Combine(MediaDir, $"{full}-oobe.xml"),
        }, 300, ct);
        ReadyCache.TryRemove(full, out _);
        Watch.Forget(full);
        return r;
    }

    public async Task<JsonElement> PurgeAsync(CancellationToken ct = default)
    {
        var r = await Run1("vm_lifecycle", new { action = "purge", tag = cfg.Tag, vmroot = VmDir, media = MediaDir }, 600, ct);
        ReadyCache.Clear();
        Watch.ClearAll();
        return r;
    }

    /// <summary>Classify a PS-Direct failure: transport death (VMGS/PowerShell
    /// gone → WinRE/WinPE/screen) vs guest-side errors (bad command, access
    /// denied) which say nothing about OS liveness.</summary>
    static void NoteExec(string vm, string err)
    {
        err = err ?? "";
        if (err.Contains("socket", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("Transport", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("session might have ended", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            Watch.MarkExecBad(vm);
    }

    /// <summary>PowerShell Direct into the guest (VMBus — works without network).</summary>
    public async Task<JsonElement> RunPsAsync(string name, string command, int timeoutSec = 60,
                                              string? user = null, string? password = null, CancellationToken ct = default)
    {
        var full = CheckName(name);
        if (IsLinuxVm(full)) return await RunLinuxSshAsync(full, command, timeoutSec, ct);
        var cmdB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));
        PsResult r;
        try
        {
            r = await ps.RunScriptAsync("guest_exec", new
            {
                vm = full, script_b64 = cmdB64,
                user = user ?? GuestDefaults.User,
                password = password ?? GuestDefaults.Password,
                timeout = timeoutSec,
            }, timeoutSec + 90, ct);
        }
        catch (Exception ex) { NoteExec(full, ex.Message); throw; }
        if (!r.Ok) { NoteExec(full, r.Error ?? "guest exec failed"); throw new InvalidOperationException(r.Error ?? "guest exec failed"); }
        var first = r.First ?? throw new InvalidOperationException("guest exec emitted nothing");
        Watch.MarkExecOk(full);
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
}
