using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace HermesHyperV.Engine;

/// <summary>
/// Runs embedded PowerShell bodies in a hidden child powershell.exe (5.1 —
/// ships with Windows, has the Hyper-V/Storage modules). LocalSystem service
/// => no scheduled task, no UAC, no inbox: we spawn directly with
/// CREATE_NO_WINDOW so there is never a console flash.
///
/// Each embedded script is a JSON-RPC-ish helper: it reads request JSON from
/// env HV_REQ (base64 utf8), executes, and prints one compact JSON object to
/// stdout: { "ok": bool, "data": [...], "error": null|string }.
/// </summary>
public sealed class PowerShellRunner(ILogger<PowerShellRunner> log)
{
    static PowerShellRunner()
    {
        // decode embedded .ps1 files (written from C# with \r\n? we normalize)
    }

    /// <summary>Run an embedded resource script (Scripts/X.ps1) with a request
    /// payload; returns parsed {ok,data,error}.</summary>
    public async Task<PsResult> RunScriptAsync(string resourceName, object? request, int timeoutSeconds, CancellationToken ct = default)
    {
        var body = ResourceScripts.Get(resourceName);
        var reqB64 = request is null ? "" : Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request)));
        var script = $"$env:HV_REQ='{reqB64}'\n{body}";

        ProcessStartInfo psi;
        string? tmpFile = null;
        // -EncodedCommand dies past ~32 KB of command line (vm_lifecycle.ps1 is
        // right at the limit). Fall back to a temp -File for big payloads.
        if (Encode(script).Length + 80 < 28000)
        {
            psi = BasePsi($"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {Encode(script)}");
        }
        else
        {
            tmpFile = Path.Combine(Path.GetTempPath(), $"hvsvc_{Guid.NewGuid():N}.ps1");
            await File.WriteAllTextAsync(tmpFile, script, new UTF8Encoding(true), ct);
            psi = BasePsi($"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tmpFile}\"");
        }

        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("powershell.exe failed to start");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return new PsResult(false, null, $"powershell timed out after {timeoutSeconds}s");
        }

        if (tmpFile is not null) { try { File.Delete(tmpFile); } catch { } }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var json = ExtractJson(stdout);
        if (json is null)
        {
            var err = string.Join("\n", new[] { stderr.Trim(), stdout.Trim() }.Where(s => s.Length > 0)).Trim();
            log.LogWarning("ps run {Name} failed ({Ms}ms): {Err}", resourceName, sw.ElapsedMilliseconds, Truncate(err, 500));
            return new PsResult(false, null, err.Length == 0 ? $"powershell exit {proc.ExitCode}, no output" : Truncate(err, 2000));
        }
        var res = JsonSerializer.Deserialize<PsResult>(json, PsResult.Options);
        log.LogInformation("ps run {Name} ok={Ok} in {Ms}ms", resourceName, res?.Ok, sw.ElapsedMilliseconds);
        return res ?? new PsResult(false, null, "unparseable result json");
    }

    /// <summary>
    /// Find the result object in a noisy stdout stream: the LAST line that
    /// starts with {"ok":true/false (the final ConvertTo-Json). Inner emitted
    /// objects also start with {" but only the result's "ok" is a bool.
    /// </summary>
    static string? ExtractJson(string s)
    {
        string? best = null;
        foreach (var line in s.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("{\"ok\":true", StringComparison.Ordinal) ||
                t.StartsWith("{\"ok\":false", StringComparison.Ordinal))
                best = t;
        }
        if (best is null) return null;
        // brace-match to be safe against trailing junk on the same line
        int depth = 0, end = -1; bool inStr = false; char prev = '\0';
        for (int i = 0; i < best.Length; i++)
        {
            var c = best[i];
            if (inStr) { if (c == '\"' && prev != '\\') inStr = false; prev = c; continue; }
            if (c == '\"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}') { depth--; if (depth == 0) { end = i; break; } }
            prev = c;
        }
        return end < 0 ? null : best.Substring(0, end + 1);
    }

    static ProcessStartInfo BasePsi(string args) => new()
    {
        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
        Arguments = args,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        StandardOutputEncoding = Encoding.UTF8,
    };

    static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

public sealed record PsResult(bool Ok, JsonElement[]? Data, string? Error)
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>First emitted object, or null.</summary>
    public JsonElement? First => Data is { Length: > 0 } ? Data[0] : null;

    public JsonElement Require()
    {
        if (!Ok) throw new InvalidOperationException(Error ?? "powershell step failed");
        if (First is null) throw new InvalidOperationException("powershell emitted no data");
        return First.Value;
    }
}
