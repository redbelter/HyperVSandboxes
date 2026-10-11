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
        [Description("0 = per-SKU default (Server 4096, Win11 8192)")] int memory_mb = 0,
        int max_memory_mb = 0, int cpu = 2,
        [Description("0 = catalog default (64)")] int disk_gb = 0,
        string? password = null)
        => JsonSerializer.SerializeToElement(await engine.CreateVmAsync(
            name, iso, edition, memory_mb, max_memory_mb, cpu, disk_gb, password, ct));

    [McpServerTool, Description("Run PowerShell INSIDE the guest over PowerShell Direct (VMBus — no network needed). Returns stdout.")]
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
