using System.Globalization;
using System.Text.Json;

namespace HermesHyperV.Engine;

/// <summary>
/// Lease registry for shared hosts: who holds which sandbox, why, until when —
/// readable by the other agents driving the same MCP server. Survives service
/// restarts (JSON file under HYPERV_ROOT). Guest activity (run_ps, terminal
/// connect) refreshes an existing lease; delete/purge keep leased VMs unless
/// forced. Touch() extends with the SAME holder/reason; call Checkout to take
/// over a claim outright.
/// </summary>
public static class CheckoutStore
{
    sealed record Lease(string Who, string Reason, int TtlMin, DateTime CreatedAt, DateTime ExpiresAt);
    sealed record LeaseDto(string Name, string Who, string Reason, int TtlMin, string CreatedAt, string ExpiresAt);

    static readonly object Gate = new();
    static readonly Dictionary<string, Lease> Rows = new(StringComparer.OrdinalIgnoreCase);
    static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };
    static bool _loaded;
    static DateTime _lastSaved = DateTime.MinValue;

    /// <summary>Test seam: forget the cached table so a test can seed its own
    /// store file (unique HYPERV_ROOT) and observe load/prune behavior.</summary>
    internal static void ResetForTests()
    {
        lock (Gate) { Rows.Clear(); _loaded = false; _lastSaved = DateTime.MinValue; }
    }

    static string StorePath => Path.Combine(
        Environment.GetEnvironmentVariable("HYPERV_ROOT") ?? @"C:\HyperV", "checkouts.json");

    static DateTime P(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    static DateTime P2(string? s, DateTime fallback)
    {
        try { return string.IsNullOrEmpty(s) ? fallback : P(s); } catch { return fallback; }
    }

    /// <summary>Claim a sandbox name (works even before create_vm). Default
    /// window 120 min, clamped to [5, 1440]. Overwrites any previous claim.</summary>
    public static Dictionary<string, object?> Checkout(string name, string who, string? reason, int minutes)
    {
        lock (Gate)
        {
            EnsureLoaded();
            who = string.IsNullOrWhiteSpace(who) ? "anonymous" : who.Trim();
            var ttl = minutes <= 0 ? 120 : Math.Clamp(minutes, 5, 1440);
            var now = DateTime.UtcNow;
            Rows[name] = new Lease(who, reason?.Trim() ?? "", ttl, now, now.AddMinutes(ttl));
            SaveLocked();
            return ToStatus(Rows[name], now)!;
        }
    }

    public static bool Release(string name)
    {
        lock (Gate)
        {
            EnsureLoaded();
            var had = Rows.Remove(name);
            if (had) SaveLocked();
            return had;
        }
    }

    /// <summary>Guest activity refreshes an existing lease (even a just-expired
    /// one — the holder is clearly still working); no lease stays no lease.</summary>
    public static void Touch(string name)
    {
        lock (Gate)
        {
            EnsureLoaded();
            if (!Rows.TryGetValue(name, out var le)) return;
            var now = DateTime.UtcNow;
            Rows[name] = le with { ExpiresAt = now.AddMinutes(le.TtlMin) };
            // hot path: in-memory renewal always applies; the disk write is
            // debounced so prober pulses don't convoy on file I/O
            if (now - _lastSaved >= TimeSpan.FromSeconds(30)) SaveLocked();
        }
    }

    /// <summary>Active lease for the dashboard/API (null = none/expired).</summary>
    public static Dictionary<string, object?>? Status(string name)
    {
        lock (Gate)
        {
            EnsureLoaded();
            if (!Rows.TryGetValue(name, out var le)) return null;
            return ToStatus(le, DateTime.UtcNow);
        }
    }

    static Dictionary<string, object?>? ToStatus(Lease le, DateTime now)
    {
        var left = (int)(le.ExpiresAt - now).TotalSeconds;
        if (left <= 0) return null;   // expired = invisible, revivable via Touch/Checkout
        return new Dictionary<string, object?>
        {
            ["who"] = le.Who,
            ["reason"] = le.Reason,
            ["expires_at"] = le.ExpiresAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["remaining_s"] = left,
        };
    }

    /// <summary>One-lock view: name + live lease, no re-read races.</summary>
    public static List<KeyValuePair<string, Dictionary<string, object?>>> Snapshot()
    {
        lock (Gate)
        {
            EnsureLoaded();
            var now = DateTime.UtcNow;
            var list = new List<KeyValuePair<string, Dictionary<string, object?>>>();
            foreach (var kv in Rows)
                if (ToStatus(kv.Value, now) is { } st)
                    list.Add(new KeyValuePair<string, Dictionary<string, object?>>(kv.Key, st));
            return list;
        }
    }

    /// <summary>Names purge should keep (delete skips them unless forced).</summary>
    public static List<string> ActiveNames()
    {
        lock (Gate)
        {
            EnsureLoaded();
            var now = DateTime.UtcNow;
            return Rows.Where(kv => kv.Value.ExpiresAt > now).Select(kv => kv.Key).ToList();
        }
    }

    public static void Forget(string name)
    {
        lock (Gate)
        {
            EnsureLoaded();
            var had = Rows.Remove(name);
            // prune other dead rows while we're here (memory mirror of SaveLocked)
            var now2 = DateTime.UtcNow;
            foreach (var k in Rows.Where(kv => kv.Value.ExpiresAt < now2.AddDays(-1))
                                  .Select(kv => kv.Key).ToList())
                Rows.Remove(k);
            if (had) SaveLocked();
        }
    }

    static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(StorePath)) return;
            var rows = JsonSerializer.Deserialize<List<LeaseDto>>(File.ReadAllText(StorePath), ReadOpts);
            var now = DateTime.UtcNow;
            foreach (var d in rows ?? [])
            {
                if (string.IsNullOrWhiteSpace(d.Name)) continue;
                DateTime exp;
                try { exp = P(d.ExpiresAt); } catch { continue; }
                if (exp < now.AddDays(-1)) continue;   // dead long enough — prune
                var ttl = d.TtlMin <= 0 ? 120 : d.TtlMin;
                Rows[d.Name] = new Lease(d.Who ?? "anonymous", d.Reason ?? "", ttl,
                                         P2(d.CreatedAt, exp.AddMinutes(-ttl)), exp);
            }
        }
        catch { /* unreadable store starts empty; first write rebuilds it */ }
    }

    static void SaveLocked()
    {
        try
        {
            var now = DateTime.UtcNow;
            var dtos = Rows
                .Where(kv => kv.Value.ExpiresAt > now.AddDays(-1))
                .Select(kv => new LeaseDto(kv.Key, kv.Value.Who, kv.Value.Reason, kv.Value.TtlMin,
                                           kv.Value.CreatedAt.ToString("o"), kv.Value.ExpiresAt.ToString("o")))
                .ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var tmp = StorePath + ".tmp";
            File.WriteAllText(tmp,
                JsonSerializer.Serialize(dtos, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, StorePath, overwrite: true);   // atomic-ish: readers never see a torn file
            _lastSaved = DateTime.UtcNow;
        }
        catch { /* metadata best-effort; never fail a sandbox operation over a lease note */ }
    }
}
