using VuStudio.Agent;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.SystemMetrics;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Configuration.AddEnvironmentVariables("VUSTUDIO_AGENT_");
// Command-line switches win over the environment.
builder.Configuration.AddCommandLine(args);

var options = AgentOptions.From(builder.Configuration);
builder.WebHost.UseUrls($"http://{options.Bind}:{options.Port}");
builder.Logging.AddSimpleConsole(c => c.SingleLine = true);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SensorHub>();
builder.Services.AddSingleton<IProviderFactory, SystemProviderFactory>();
builder.Services.AddSingleton<IProviderFactory, UnraidProviderFactory>();
builder.Services.AddSingleton<ProviderManager>();
builder.Services.AddHostedService<SensorHost>();
if (options.Mdns)
{
    builder.Services.AddHostedService<MdnsAdvertiser>();
}

var app = builder.Build();
if (options.Token is null)
{
    app.Logger.LogWarning("No token configured: anyone on the network can read this machine's sensors. Set VUSTUDIO_AGENT_TOKEN to require one");
}

AgentApi.Map(app, options, app.Services.GetRequiredService<SensorHub>(), app.Services.GetRequiredService<TimeProvider>());
app.Run();

/// <summary>Entry point marker so tests can reference the assembly.</summary>
public partial class Program;
