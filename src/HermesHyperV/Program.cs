using HermesHyperV;
using HermesHyperV.Engine;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // The exe ships with wwwroot/ beside it (publish dir). Pin content root to
    // the exe directory ALWAYS — as a service the CWD is system32, and as a
    // console process the CWD depends on whoever launched it (CI starts it via
    // Start-Process, other hosts may not). BaseDirectory is the one reliable spot.
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Host.UseWindowsService(o => o.ServiceName = "HermesHyperV");

var port = int.TryParse(Environment.GetEnvironmentVariable("HYPERV_SVC_PORT"), out var p) ? p : 5003;
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

if (WindowsServiceHelpers.IsWindowsService())
    builder.Logging.AddEventLog(o => o.SourceName = "HermesHyperV");

var root = Environment.GetEnvironmentVariable("HYPERV_ROOT") ?? @"C:\HyperV";
builder.Services.AddSingleton(new SandboxSettings(Root: root));
builder.Services.AddSingleton<PowerShellRunner>();
if (Environment.GetEnvironmentVariable("HYPERV_FAKE_PS") == "1")
    builder.Services.AddSingleton<IPowerShellRunner>(new FakeRunner());   // CI: no Hyper-V needed
else
    builder.Services.AddSingleton<IPowerShellRunner>(sp => sp.GetRequiredService<PowerShellRunner>());
builder.Services.AddSingleton<SandboxEngine>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<VmsCache>();
builder.Services.AddHostedService<SandboxEngine.ReadinessProber>();
builder.Services.AddHostedService<SandboxProxy>();   // guests' egress door (HTTP/CONNECT on :8899)
builder.Services.AddHostedService<PilotServe>();     // pilot payload door (HTTP GET on :8898)

// MCP server over streamable HTTP at /mcp (the agent-facing control plane)
builder.Services.AddMcpServer().WithHttpTransport().WithTools<HypervSandboxTools>();

var app = builder.Build();

app.UseStaticFiles(); // wwwroot/dashboard.html
app.MapGet("/", () => Results.Redirect("/dashboard.html"));
app.MapGet("/index.html", () => Results.Redirect("/dashboard.html"));
app.MapSandboxApi();
app.MapTerminal();   // /ws/term (browser ssh into Linux sandboxes)
app.MapMcp("/mcp");

app.Logger.LogInformation("HermesHyperV service starting on :{Port} (mcp=/mcp, ui=/dashboard.html)", port);
await app.RunAsync();
