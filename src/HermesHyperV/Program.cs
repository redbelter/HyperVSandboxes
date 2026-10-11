using HermesHyperV;
using HermesHyperV.Engine;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // when launched as a Windows service the CWD is system32; pin content
    // root to the exe directory so wwwroot/ is found
    ContentRootPath = WindowsServiceHelpers.IsWindowsService()
        ? AppContext.BaseDirectory : Directory.GetCurrentDirectory(),
});

builder.Host.UseWindowsService(o => o.ServiceName = "HermesHyperV");

var port = int.TryParse(Environment.GetEnvironmentVariable("HYPERV_SVC_PORT"), out var p) ? p : 5003;
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

if (WindowsServiceHelpers.IsWindowsService())
    builder.Logging.AddEventLog(o => o.SourceName = "HermesHyperV");

var root = Environment.GetEnvironmentVariable("HYPERV_ROOT") ?? @"C:\HyperV";
builder.Services.AddSingleton(new SandboxSettings(Root: root));
builder.Services.AddSingleton<PowerShellRunner>();
builder.Services.AddSingleton<SandboxEngine>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<VmsCache>();
builder.Services.AddHostedService<SandboxEngine.ReadinessProber>();

// MCP server over streamable HTTP at /mcp (the agent-facing control plane)
builder.Services.AddMcpServer().WithHttpTransport().WithTools<HypervSandboxTools>();

var app = builder.Build();

app.UseStaticFiles(); // wwwroot/dashboard.html
app.MapGet("/", () => Results.Redirect("/dashboard.html"));
app.MapGet("/index.html", () => Results.Redirect("/dashboard.html"));
app.MapSandboxApi();
app.MapMcp("/mcp");

app.Logger.LogInformation("HermesHyperV service starting on :{Port} (mcp=/mcp, ui=/dashboard.html)", port);
await app.RunAsync();
