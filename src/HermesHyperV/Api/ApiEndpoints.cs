using System.Text.Json;
using HermesHyperV.Engine;

namespace HermesHyperV;

/// <summary>
/// REST surface byte-compatible with the dashboard JS (polls /api/status,
/// /api/vms, /api/jobs, /api/log every few seconds; POST create/start/stop/
/// delete/purge/run). Same job-spawn semantics as the Flask dashboard: long
/// ops run in background, {job:id} returns immediately.
/// </summary>
public static class Api
{
    public static IEndpointRouteBuilder MapSandboxApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/status", async (SandboxEngine e) =>
        {
            try { return Results.Json(new { ok = true, data = await e.HostStatusAsync() }); }
            catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
        });

        api.MapGet("/vms", async (SandboxEngine e, VmsCache cache) =>
        {
            if (cache.Hits())
                return Results.Json(cache.Value!);
            try
            {
                var vms = await e.ListVmsAsync();
                var data = new { ok = true, data = new { vms = vms.Select(AddPhase).ToList() } };
                cache.Set(JsonSerializer.SerializeToElement(data));
                return Results.Json(data);
            }
            catch
            {
                return Results.Json(new { ok = true, data = new { vms = Array.Empty<object>() } });
            }
        });

        api.MapPost("/create", (CreateReq body, SandboxEngine e, JobStore jobs) =>
        {
            var name = string.IsNullOrWhiteSpace(body.Name)
                ? $"sandbox-{DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000}"
                : body.Name!.Trim();
            jobs.Blog($"create_vm(name=\"{name}\", iso=\"server2025-eval\")", "tool");
            var jid = jobs.Spawn("create_vm", async ct => await e.CreateVmAsync(
                name, memoryMb: body.MemoryMb ?? 4096, cpu: body.Cpu ?? 2,
                diskGb: body.DiskGb ?? 64, ct: ct));
            return Results.Json(new { job = jid });
        });

        api.MapPost("/delete", (NameReq body, SandboxEngine e, JobStore jobs) =>
        {
            jobs.Blog($"delete_vm(name=\"{body.Name}\") — VM + vhdx + folder", "tool");
            return Results.Json(new { job = jobs.Spawn("delete_vm", async ct => await e.DeleteVmAsync(body.Name!, ct)) });
        });

        api.MapPost("/purge", (SandboxEngine e, JobStore jobs) =>
        {
            jobs.Blog("purge_all() — all tagged sandboxes", "tool");
            return Results.Json(new { job = jobs.Spawn("purge_all", async ct => await e.PurgeAsync(ct)) });
        });

        api.MapPost("/stop", (NameReq body, SandboxEngine e, JobStore jobs) =>
        {
            jobs.Blog($"stop_vm(name=\"{body.Name}\")", "tool");
            return Results.Json(new { job = jobs.Spawn("stop_vm", async ct => await e.StopVmAsync(body.Name!, ct)) });
        });

        api.MapPost("/start", (NameReq body, SandboxEngine e, JobStore jobs) =>
        {
            jobs.Blog($"start_vm(name=\"{body.Name}\")", "tool");
            return Results.Json(new { job = jobs.Spawn("start_vm", async ct => await e.StartVmAsync(body.Name!, ct)) });
        });

        api.MapPost("/run", (RunReq body, SandboxEngine e, JobStore jobs) =>
        {
            var cmd = string.IsNullOrWhiteSpace(body.Cmd) ? "whoami" : body.Cmd!;
            jobs.Blog($"run_ps(\"{body.Name}\", \"{(cmd.Length > 70 ? cmd[..70] : cmd)}\")", "tool");
            var jid = jobs.Spawn("run_ps", async ct =>
                await e.RunPsAsync(body.Name!, cmd, body.TimeoutS ?? 60, ct: ct));
            return Results.Json(new { job = jid });
        });

        api.MapGet("/jobs", (JobStore jobs) => Results.Json(new { jobs = jobs.Jobs() }));

        api.MapGet("/log", (JobStore jobs, int? since) =>
        {
            var (items, n) = jobs.LogSince(since ?? 0);
            return Results.Json(new { log = items, n });
        });

        return app;
    }

    static JsonElement AddPhase(JsonElement vm)
    {
        var o = JsonSerializer.Deserialize<Dictionary<string, object?>>(vm.GetRawText())!;
        o["phase"] = SandboxEngine.Phase(vm);
        return JsonSerializer.SerializeToElement(o);
    }

    public sealed record NameReq(string? Name);
    public sealed record RunReq([property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name,
                                [property: System.Text.Json.Serialization.JsonPropertyName("cmd")] string? Cmd,
                                [property: System.Text.Json.Serialization.JsonPropertyName("timeout_s")] int? TimeoutS);
    public sealed record CreateReq([property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name,
                                   [property: System.Text.Json.Serialization.JsonPropertyName("memory_mb")] int? MemoryMb,
                                   [property: System.Text.Json.Serialization.JsonPropertyName("cpu")] int? Cpu,
                                   [property: System.Text.Json.Serialization.JsonPropertyName("disk_gb")] int? DiskGb);
}

/// <summary>4s cache on the VM list, same as Flask's _vms_cache.</summary>
public sealed class VmsCache
{
    (DateTime T, JsonElement Data)? _cache;
    readonly SemaphoreSlim _gate = new(1, 1);

    public bool Hits() => _cache is { } c && (DateTime.UtcNow - c.T).TotalSeconds < 4;
    public JsonElement? Value => _cache?.Data;

    public void Set(JsonElement data) => _cache = (DateTime.UtcNow, data);
}
