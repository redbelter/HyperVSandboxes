using System.Text.Json;
using HermesHyperV.Engine;
using Xunit;

namespace HermesHyperV.Tests;

/// <summary>
/// Deterministic engine-level tests over the fake PS runner contract.
/// Every VM name is unique per test (Watch/ReadyCache are process-wide statics).
/// </summary>
public class EngineTests
{
    static SandboxEngine MakeEngine(out FakeRunner fake, string root)
    {
        Directory.CreateDirectory(root);
        var log = Microsoft.Extensions.Logging.Abstractions.NullLogger<SandboxEngine>.Instance;
        fake = new FakeRunner();
        return new SandboxEngine(fake, new SandboxSettings(Root: root), log);
    }

    static JsonElement Row(string name, string state = "Running", string hb = "OkApplicationsUnknown",
                           bool linux = false, string uptime = "0 00:05:00", string osrep = "Server 2025") =>
        JsonDocument.Parse($$"""
        {"name":"{{name}}","state":"{{state}}","heartbeat":"{{hb}}","linux":{{(linux ? "true" : "false")}},
         "cpu_count":2,"mem_mb":2048,"uptime":"{{uptime}}","ips":[],"os_reported":"{{osrep}}"}
        """).RootElement.Clone();

    [Theory]
    [InlineData("win1", "mcp-win1")]
    [InlineData("mcp-win1", "mcp-win1")]
    [InlineData("Debian_2", "mcp-Debian_2")]
    public void CheckName_prefixes_and_validates(string input, string expected) =>
        Assert.Equal(expected, SandboxEngine.CheckName(input));

    [Theory]
    [InlineData("")]
    [InlineData("-lead")]
    [InlineData("has space")]
    [InlineData("ünïcode")]
    public void CheckName_rejects_bad_names(string input) =>
        Assert.Throws<ArgumentException>(() => SandboxEngine.CheckName(input));

    [Fact]
    public async Task ListVms_stamps_phase_and_checkout()
    {
        var root = TestRoot("list");
        var e = MakeEngine(out _, root);
        var vms = await e.ListVmsAsync();
        var row = Assert.Single(vms);
        Assert.Equal("Installing", row.GetProperty("phase").GetString());   // hb Ok, no ready marker yet
        Assert.True(row.TryGetProperty("checkout", out _));                  // field always present
    }

    [Fact]
    public void Phase_obeys_state_machine()
    {
        // off -> Off regardless of heartbeat
        Assert.Equal("Off", SandboxEngine.Phase(Row("p-off", state: "Off")));
        // heartbeat Ok + desktop marker -> Ready
        SandboxEngine.ReadyCache["p-ready"] = "ready";
        Assert.Equal("Ready", SandboxEngine.Phase(Row("p-ready")));
        // linux guest with marker -> Ready even though heartbeat never leaves Unknown
        SandboxEngine.ReadyCache["p-lnx"] = "ready";
        Assert.Equal("Ready", SandboxEngine.Phase(Row("p-lnx", hb: "Unknown", linux: true)));
        // heartbeat dark, never reported, inside boot budget -> honest Installing
        Assert.Equal("Installing", SandboxEngine.Phase(Row("p-dark", hb: "NoContact", osrep: "")));
    }

    [Fact]
    public void Watch_verdicts_are_honest()
    {
        var jobs = new JobStore(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobStore>.Instance);
        // booted once (EverOk from Ok heartbeat), then heartbeat dark past the
        // flicker grace -> lost-contact verdict. Age the dark-since past 90s.
        SandboxEngine.Watch.Observe("w-lost", Row("w-lost"), jobs);
        SandboxEngine.Watch.DarkSince["w-lost"] = DateTime.UtcNow - TimeSpan.FromSeconds(120);
        SandboxEngine.Watch.Observe("w-lost", Row("w-lost", hb: "NoContact"), jobs);
        Assert.Contains("Lost contact", SandboxEngine.Watch.Verdict("w-lost"));

        // kernel alive but shell transport dead >2min -> WinRE-style verdict
        SandboxEngine.Watch.Observe("w-re", Row("w-re"), jobs);
        SandboxEngine.Watch.MarkExecOk("w-re");
        SandboxEngine.Watch.MarkExecBad("w-re");
        // age the bad mark past the 2-min grace via a fresh bad stamp after ok is
        // not possible without waiting; instead verify the fresh-failure grace first
        Assert.Null(SandboxEngine.Watch.Verdict("w-re"));   // shell death still inside grace
        Assert.True(SandboxEngine.Watch.OsReported("w-re"));

        // Linux rows never get a stuck verdict (no heartbeat IC by design)
        SandboxEngine.Watch.Observe("w-lnx", Row("w-lnx", hb: "Unknown", linux: true), jobs);
        Assert.Null(SandboxEngine.Watch.Verdict("w-lnx"));
    }

    [Fact]
    public async Task CreateWindows_builds_unattend_and_records_child_call()
    {
        var root = TestRoot("cw");
        Directory.CreateDirectory(Path.Combine(root, "Media"));
        File.WriteAllText(Path.Combine(root, "Media", "x.iso"), "iso");     // any ISO on disk works
        var e = MakeEngine(out var fake, root);

        var res = await e.CreateVmAsync("cw", "x.iso", edition: "Std", ct: default);

        Assert.Equal("mcp-cw", res["vm"]);
        Assert.Equal("Sandbox", res["guest_user"]);
        Assert.False(string.IsNullOrEmpty(res["guest_password"]?.ToString()));

        // unattend rendered: OOBE skips + bootstrap handoff, no placeholders left
        var oobe = File.ReadAllText(Path.Combine(root, "Media", "mcp-cw-oobe.xml"));
        Assert.Contains("<SkipMachineOOBE>true</SkipMachineOOBE>", oobe);
        Assert.Contains("<SkipUserOOBE>true</SkipUserOOBE>", oobe);
        Assert.Contains("prov_first.ps1", oobe);                           // hands off to the bootstrap
        Assert.DoesNotContain("__PASSWORD__", oobe);
        Assert.DoesNotContain("__COMPUTERNAME__", oobe);
        // pilot payload door is baked into the first-logon bootstrap script
        var prov = File.ReadAllText(Path.Combine(root, "Media", "mcp-cw-prov_first.ps1"));
        Assert.Contains("http://192.168.50.1:", prov);

        // child_disk called with the rendered unattend; vm_lifecycle create follows
        var child = Assert.Single(fake.CallsFor("child_disk"));
        Assert.Equal(Path.Combine(root, "Media", "mcp-cw-oobe.xml"), child.GetProperty("unattend").GetString());
        var create = fake.CallsFor("vm_lifecycle")
            .First(r => r.TryGetProperty("action", out var a) && a.GetString() == "create");
        Assert.Equal("mcp-cw", create.GetProperty("name").GetString());
        Assert.Equal(2, create.GetProperty("cpu").GetInt32());

        // creds beside the VM
        var creds = File.ReadAllText(Path.Combine(root, "VMs", "mcp-cw", "creds.json"));
        Assert.Contains("\"user\":\"Sandbox\"", creds.Replace(" ", ""));
    }

    [Fact]
    public async Task CreateLinux_assigns_distinct_static_ips_across_calls()
    {
        var root = TestRoot("cl");
        Directory.CreateDirectory(Path.Combine(root, "Media"));
        File.WriteAllText(Path.Combine(root, "Media", "debian-13-generic-amd64.raw"), "raw");
        var e = MakeEngine(out var fake, root);

        var a = await e.CreateLinuxVmAsync("cl-a", "debian-13", ct: default);
        var b = await e.CreateLinuxVmAsync("cl-b", "debian-13", ct: default);

        var ipA = a["ip"]!.ToString();
        var ipB = b["ip"]!.ToString();
        Assert.StartsWith("192.168.50.", ipA);
        Assert.StartsWith("192.168.50.", ipB);
        Assert.NotEqual(ipA, ipB);   // hole-finding survives without the old in-memory counter bugs
        Assert.Equal("sandbox", a["guest_user"]);

        // second call's seed carries its own ip + hostname derived from vm name
        var seeds = fake.CallsFor("vm_lifecycle")
            .Where(r => r.TryGetProperty("action", out var a2) && a2.GetString() == "linux_create").ToList();
        Assert.Equal(2, seeds.Count);
        Assert.Equal("cl-b", seeds[1].GetProperty("hostname").GetString());
        Assert.Equal(ipB, seeds[1].GetProperty("ip").GetString());
    }

    [Fact]
    public async Task RunPs_windows_goes_through_guest_exec_b64()
    {
        var root = TestRoot("rp");
        var e = MakeEngine(out var fake, root);
        var r = await e.RunPsAsync("rp", "whoami", 30);
        Assert.Equal("exec:whoami", r.GetProperty("output").GetString());
        Assert.False(r.GetProperty("timed_out").GetBoolean());
        var call = Assert.Single(fake.CallsFor("guest_exec"));
        Assert.Equal("mcp-rp", call.GetProperty("vm").GetString());
    }

    [Fact]
    public async Task RunPs_linux_uses_ssh_and_filters_hostkey_warning()
    {
        var root = TestRoot("rl");
        Directory.CreateDirectory(Path.Combine(root, "Media"));
        File.WriteAllText(Path.Combine(root, "Media", "debian-13-generic-amd64.raw"), "raw");
        var e = MakeEngine(out _, root);
        await e.CreateLinuxVmAsync("rl", "debian-13", ct: default);   // sets meta.json via fake
        var r = await e.RunPsAsync("rl", "hostname", 30);
        Assert.Equal("exec:hostname", r.GetProperty("output").GetString());
        Assert.Equal(0, r.GetProperty("exit").GetInt32());
        // the fake emits the first-connect warning line; engine must drop exactly it
        Assert.DoesNotContain("Permanently added", r.GetProperty("stderr").GetString() ?? "");
    }

    [Fact]
    public async Task VmHealth_returns_signals_from_fake_diag()
    {
        var root = TestRoot("vh");
        var e = MakeEngine(out _, root);
        var h = await e.VmHealthAsync("win1");
        Assert.Equal("mcp-win1", h.GetProperty("vm").GetString());
        Assert.True(h.GetProperty("os_reported").GetBoolean());        // KVP present in fake row
        Assert.Equal("healthy", h.GetProperty("diagnosis").GetString());
        Assert.NotEmpty(h.GetProperty("recent_events").EnumerateArray());
    }

    [Fact]
    public void Leases_roundtrip_through_store()
    {
        var st = SandboxEngine.CheckoutVm("ls", "ci-runner", "smoke suite", 30);
        Assert.Equal("ci-runner", st.GetProperty("who").GetString());
        Assert.True(st.GetProperty("remaining_s").GetInt32() > 1700);

        var listed = SandboxEngine.ListCheckouts().EnumerateArray()
            .First(x => x.GetProperty("name").GetString() == "mcp-ls");
        Assert.Equal("smoke suite", listed.GetProperty("lease").GetProperty("reason").GetString());

        // a second claim overwrites cleanly
        SandboxEngine.CheckoutVm("ls", "ci-runner-2", null, 10);
        var listed2 = SandboxEngine.ListCheckouts().EnumerateArray()
            .First(x => x.GetProperty("name").GetString() == "mcp-ls");
        Assert.Equal("ci-runner-2", listed2.GetProperty("lease").GetProperty("who").GetString());

        Assert.True(SandboxEngine.ReleaseVm("ls").GetProperty("had").GetBoolean());
        Assert.False(SandboxEngine.ReleaseVm("ls").GetProperty("had").GetBoolean());   // idempotent
    }

    [Fact]
    public async Task Delete_skips_leased_unless_forced()
    {
        var root = TestRoot("dl");
        var e = MakeEngine(out _, root);
        SandboxEngine.CheckoutVm("dl", "holder-x", "busy", 30);

        var skipped = await e.DeleteVmAsync("dl");
        Assert.False(skipped.GetProperty("deleted").GetBoolean());
        Assert.Equal("holder-x", skipped.GetProperty("holder").GetString());

        var forced = await e.DeleteVmAsync("dl", force: true);
        Assert.True(forced.GetProperty("deleted").GetBoolean());
    }

    [Fact]
    public async Task Activity_extends_lease()
    {
        var root = TestRoot("tz");
        var e = MakeEngine(out _, root);
        SandboxEngine.CheckoutVm("tz", "me", "ttl", 10);

        await Task.Delay(1100);                       // let the clock visibly tick
        var before = Convert.ToInt32(CheckoutStore.Status("mcp-tz")!["remaining_s"]);

        await e.RunPsAsync("tz", "echo hi", 20);      // exec marks the holder alive
        var after = Convert.ToInt32(CheckoutStore.Status("mcp-tz")!["remaining_s"]);

        Assert.True(after > before, $"expected renewal: {before} -> {after}");
    }

    static string TestRoot(string tag) =>
        Path.Combine(Path.GetTempPath(), "hvsvc-test-" + tag + "-" + Guid.NewGuid().ToString("N")[..8]);
}

// (checkout status is a Dictionary — no extension helpers needed)
