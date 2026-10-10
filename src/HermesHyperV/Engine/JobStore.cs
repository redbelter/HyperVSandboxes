using System.Text.Json;
using System.Text.Json.Nodes;

namespace HermesHyperV.Engine;

public sealed record Job(string Id, string Name, string Status, long Started)
{
    public object? Result { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// In-memory job + activity store, exactly the shapes the dashboard JS
/// polls: /api/jobs -> {jobs:[{id,name,status,result,error}]},
/// /api/log?since=N -> {log:[{n,t,m,k}], n}.
/// </summary>
public sealed class JobStore(ILogger<JobStore> log)
{
    readonly Dictionary<string, JsonObject> _jobs = new();
    readonly Queue<(long N, string T, string M, string K)> _logq = new();
    readonly object _gate = new();
    long _seq;

    public string Blog(string msg, string kind = "")
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        lock (_gate)
        {
            _seq++;
            _logq.Enqueue((_seq, DateTime.Now.ToString("HH:mm:ss"), msg, kind));
            while (_logq.Count > 300) _logq.Dequeue();
        }
        log.LogInformation("activity {Kind} {Msg}", string.IsNullOrEmpty(kind) ? "-" : kind, msg);
        return line;
    }

    public string Spawn(string name, Func<CancellationToken, Task<object?>> work)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        lock (_gate)
            _jobs[id] = new JsonObject { ["id"] = id, ["name"] = name, ["status"] = "running", ["started"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        Blog($"{name}(...) started", "tool");

        _ = Task.Run(async () =>
        {
            try
            {
                var res = await work(CancellationToken.None);
                Finish(id, "done", res, null);
                Blog($"{name} finished", "ok");
            }
            catch (Exception e)
            {
                Finish(id, "error", null, e.Message);
                Blog($"{name} FAILED: {Trunc(e.Message, 300)}", "err");
            }
        });
        return id;
    }

    void Finish(string id, string status, object? result, string? error)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var j)) return;
            j["status"] = status;
            j["result"] = result is null ? null : ToNode(result);
            if (error is not null) j["error"] = error;
        }
    }

    static JsonNode? ToNode(object o) => o switch
    {
        JsonElement je => JsonNode.Parse(je.GetRawText()),
        JsonNode jn => jn.DeepClone(),
        _ => JsonSerializer.SerializeToNode(o),
    };

    public List<JsonObject> Jobs()
    {
        lock (_gate) return _jobs.Values.Select(j => (JsonObject)j.DeepClone()).ToList();
    }

    public (List<JsonObject> Items, long N) LogSince(int since)
    {
        lock (_gate)
        {
            var items = _logq.Where(x => x.N > since).Select(x => new JsonObject
            {
                ["n"] = x.N, ["t"] = x.T, ["m"] = x.M, ["k"] = x.K,
            }).ToList();
            return (items, _seq);
        }
    }

    static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];
}
