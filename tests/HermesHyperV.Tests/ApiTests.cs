using System.Net;
using HermesHyperV.Engine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HermesHyperV.Tests;

/// <summary>
/// Full-pipeline tests: the real REST/MCP/dashboard surface over the fake PS
/// runner. No Hyper-V needed — CI-safe. HYPERV_FAKE_PS=1 makes Program.cs
/// register the in-process FakeRunner; the harness also swaps one in so the
/// order of registration doesn't matter.
/// </summary>
public class ApiTests : IDisposable
{
    readonly WebApplicationFactory<Program> _app;
    HttpClient _http;

    public ApiTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvsvc-api-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("HYPERV_FAKE_PS", "1");
        Environment.SetEnvironmentVariable("HYPERV_ROOT", root);

        _app = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("https_port", "");   // not needed here
                b.ConfigureTestServices(s =>
                {
                    s.AddSingleton<IPowerShellRunner>(new FakeRunner());
                });
            });
        _http = _app.CreateClient();
    }

    public void Dispose()
    {
        _http.Dispose();
        _app.Dispose();
        Environment.SetEnvironmentVariable("HYPERV_FAKE_PS", null);
        Environment.SetEnvironmentVariable("HYPERV_ROOT", null);
    }

    [Fact]
    public async Task Status_ok_hypervisor_present()
    {
        var r = await _http.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = await Json(r);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("data").GetProperty("hypervisor_present").GetBoolean());
    }

    [Fact]
    public async Task Catalog_lists_three_skus()
    {
        using var doc = await Json(await _http.GetAsync("/api/catalog"));
        var keys = doc.RootElement.GetProperty("data").GetProperty("isos")
            .EnumerateArray().Select(x => x.GetProperty("key").GetString()).ToList();
        Assert.Contains("server2025-eval", keys);
        Assert.Contains("win11ent-26h2", keys);
        Assert.Contains("debian-13", keys);
    }

    [Fact]
    public async Task Vms_rows_carry_phase_and_checkout()
    {
        using var doc = await Json(await _http.GetAsync("/api/vms"));
        var rows = doc.RootElement.GetProperty("data").GetProperty("vms").EnumerateArray().ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, v => Assert.False(string.IsNullOrEmpty(v.GetProperty("phase").GetString())));
        Assert.All(rows, v => Assert.True(v.TryGetProperty("checkout", out _)));
    }

    [Fact]
    public async Task Dashboard_serves_with_adaptive_markers()
    {
        var html = await _http.GetStringAsync("/dashboard.html");
        foreach (var marker in new[] { "selectVM", "syncConsole", "SSH terminal", "PowerShell Direct" })
            Assert.Contains(marker, html);
        Assert.Contains("favicon.svg", html);
    }

    [Fact]
    public async Task Favicon_serves_svg()
    {
        var r = await _http.GetAsync("/favicon.svg");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("svg", (await r.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Create_run_release_cycle_over_http()
    {
        // create (queued job) -> jobs list shows it
        using (var doc = await Post("/api/create", """{"name":"api","iso":"debian-13","ttl":"ignored"}"""))
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("job").GetString()));

        // checkout for the api test, then list it back
        using (var doc = await Post("/api/checkout", """{"name":"api","who":"apitest","reason":"e2e","ttl_min":30}"""))
            Assert.Equal("apitest", doc.RootElement.GetProperty("data").GetProperty("who").GetString());

        using (var doc = await Json(await _http.GetAsync("/api/checkouts")))
        {
            var hit = doc.RootElement.GetProperty("data").EnumerateArray()
                .First(x => x.GetProperty("name").GetString() == "mcp-api");
            Assert.Equal("e2e", hit.GetProperty("lease").GetProperty("reason").GetString());
        }

        // release then it disappears
        await Post("/api/release", """{"name":"api"}""");
        using (var doc = await Json(await _http.GetAsync("/api/checkouts")))
            Assert.DoesNotContain(doc.RootElement.GetProperty("data").EnumerateArray(),
                x => x.GetProperty("name").GetString() == "mcp-api");
    }

    [Fact]
    public async Task Mcp_tools_roundtrip()
    {
        var msg = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_vms","arguments":{}}}""";
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(msg, System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("Accept", "application/json, text/event-stream");
        var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("mcp-win1", body);       // fake list row surfaced through MCP too
    }

    static async Task<System.Text.Json.JsonDocument> Json(HttpResponseMessage r)
    {
        r.EnsureSuccessStatusCode();
        return System.Text.Json.JsonDocument.Parse(await r.Content.ReadAsStringAsync());
    }

    async Task<System.Text.Json.JsonDocument> Post(string path, string body)
    {
        var resp = await _http.PostAsync(path, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        return await Json(resp);
    }
}
