using System.ComponentModel;
using System.Text.Json;
using HermesHyperV.Engine;
using ModelContextProtocol.Server;

namespace HermesHyperV;

/// <summary>
/// The MCP surface: same tool names as the proven python server, now served
/// over streamable-HTTP at /mcp by the Windows service itself.
/// </summary>
[McpServerToolType]
public sealed class HypervSandboxTools(SandboxEngine engine)
{
    [McpServerTool, Description("Host readiness for sandboxing: VMMS, hypervisor, ISOs, free disk.")]
    public async Task<JsonElement> Sandbox_status(CancellationToken ct) =>
        JsonSerializer.SerializeToElement(await engine.HostStatusAsync(ct));

    [McpServerTool, Description("List this server's sandbox VMs (tagged ones only) with state, heartbeat, IPs.")]
    public async Task<JsonElement> List_vms(CancellationToken ct)
    {
        var vms = await engine.ListVmsAsync(ct);
        return JsonSerializer.SerializeToElement(new { vms = vms.Select(AddPhase).ToList() });
    }

    static JsonElement AddPhase(JsonElement vm)
    {
        var o = JsonSerializer.Deserialize<Dictionary<string, object?>>(vm.GetRawText())!;
        o["phase"] = SandboxEngine.Phase(vm);
        return JsonSerializer.SerializeToElement(o);
    }

    [McpServerTool, Description("Full status of one sandbox VM (state, heartbeat, guest IPs). heartbeat=Ok means the guest OS is up and run_ps will answer.")]
    public async Task<JsonElement> Vm_status(string name, CancellationToken ct)
    {
        var vm = await engine.VmStatusAsync(name, ct);
        return AddPhase(vm);
    }

    [McpServerTool, Description(
        "Health verdict for one sandbox VM from HOST-SIDE signals: heartbeat, KVP " +
        "OS self-report, uptime, recent Hyper-V worker/VMMS events. Use when a VM " +
        "is Running but never Ready or run_ps keeps failing — the diagnosis names " +
        "the likely state (still installing / crashed out of a booted OS / stuck " +
        "at boot screen or WinRE). Works even when the guest OS is down.")]
    public Task<JsonElement> Vm_health(string name, CancellationToken ct) =>
        engine.VmHealthAsync(name, ct);

    [McpServerTool, Description("List Windows editions inside an ISO (catalog name, file in C:\\HyperV\\Media, or absolute path).")]
    public Task<JsonElement> List_editions(string iso, CancellationToken ct) =>
        engine.ListEditionsAsync(iso, ct);

    [McpServerTool, Description(
        "Create AND start a throwaway Gen-2 VM with Windows PRE-INSTALLED (offline " +
        "provisioning: the host applies the image into the VHDX, sets up EFI boot, the " +
        "VM boots straight into auto-OOBE — no boot keys needed). BLOCKS ~8-12 min during " +
        "image apply, then returns with the VM running; the guest answers run_ps within " +
        "~2-4 min after that (heartbeat Ok). Guest account 'Sandbox'. Disposable: wipe " +
        "it with delete_vm.")]
    public async Task<JsonElement> Create_vm(
        string name, CancellationToken ct,
        [Description("catalog name 'server2025-eval' | 'win11ent-26h2', file name in C:\\HyperV\\Media, or absolute path")] string iso = "server2025-eval",
        string? edition = null,
        [Description("0 = per-SKU default (Server 4096, Win11 8192, Debian 2048)")] int memory_mb = 0,
        int max_memory_mb = 0, int cpu = 2,
        [Description("0 = catalog default (64)")] int disk_gb = 0,
        string? password = null)
        => JsonSerializer.SerializeToElement(IsoCatalog.IsLinux(iso)
            ? await engine.CreateLinuxVmAsync(name, iso, memory_mb, cpu, disk_gb, password, ct)
            : await engine.CreateVmAsync(
                name, iso, edition, memory_mb, max_memory_mb, cpu, disk_gb, password, ct));

    [McpServerTool, Description("BLOCK until the VM's desktop is genuinely usable: polls the " +
        "FirstLogonCommands marker until the guest has logged on to the desktop. THIS is 'the VM " +
        "is ready' — heartbeat/state only prove the OS kernel responds; run_ps works even while " +
        "OOBE is stalled. Call after create_vm before using a fresh VM. Returns ready_at timestamp.")]
    public async Task<JsonElement> Wait_ready(
        string name, CancellationToken ct,
        [Description("max seconds to wait (default 300, max 3600)")] int timeout_s = 300)
        => JsonSerializer.SerializeToElement(await engine.WaitReadyAsync(name, timeout_s, ct));

    [McpServerTool, Description("Run a command INSIDE the guest: PowerShell via PowerShell Direct on "
        + "Windows VMs, bash via SSH on Linux VMs. Returns stdout, stderr, exit code.")]
    public Task<JsonElement> Run_ps(string name, string command, CancellationToken ct, int timeout_s = 60) =>
        engine.RunPsAsync(name, command, timeout_s, ct: ct);

    [McpServerTool, Description("Start a stopped sandbox VM.")]
    public Task<JsonElement> Start_vm(string name, CancellationToken ct) => engine.StartVmAsync(name, ct);

    [McpServerTool, Description("Stop (force-off) a running sandbox VM.")]
    public Task<JsonElement> Stop_vm(string name, CancellationToken ct) => engine.StopVmAsync(name, ct);

    [McpServerTool, Description("Destroy a sandbox VM and its VHDX/folder. Refuses VMs not tagged as sandboxes.")]
    public Task<JsonElement> Delete_vm(string name, CancellationToken ct) => engine.DeleteVmAsync(name, ct);

    [McpServerTool, Description("Destroy ALL tagged sandbox VMs and their files (nukes this host's fleet).")]
    public Task<JsonElement> Purge_all(CancellationToken ct) => engine.PurgeAsync(ct);
}
