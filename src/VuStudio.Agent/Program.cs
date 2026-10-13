using VuStudio.Agent;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.SystemMetrics;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Configuration.AddEnvironmentVariables("VUSTUDIO_AGENT_");
// Command-line switches win over the environment.
builder.Configuration.AddCommandLine(args);

var configured = AgentOptions.From(builder.Configuration);
// Reachable from the network without a token: generate one (unless VUSTUDIO_AGENT_INSECURE=1).
var tokenOutcome = AgentSecurity.EnsureToken(configured, AgentSecurity.TokenFile(configured));
var options = tokenOutcome.Options;
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
// A loopback-only agent is unreachable from the LAN, so announcing it would only mislead the service.
if (options.Mdns && !AgentSecurity.IsLoopbackBind(options.Bind))
{
    builder.Services.AddHostedService<MdnsAdvertiser>();
}

var app = builder.Build();
switch (tokenOutcome.Source)
{
    case TokenSource.Insecure:
        app.Logger.LogWarning("VUSTUDIO_AGENT_INSECURE is set: no token, anyone on the network can read this machine's sensors");
        break;
    case TokenSource.Generated:
        app.Logger.LogWarning(
            "\n==================================================================\n" +
            " No token was configured, so one was generated (saved in {Path}).\n" +
            " Enter it in VU Studio as the agent's access token:\n\n    {Token}\n" +
            "==================================================================",
            tokenOutcome.Path,
            options.Token);
        break;
    case TokenSource.Ephemeral:
        app.Logger.LogError(
            "No token was configured and the generated one could not be saved in {Path} ({Error}); it changes at every start. " +
            "Set VUSTUDIO_AGENT_TOKEN or make the directory writable. Token for this run: {Token}",
            tokenOutcome.Path,
            tokenOutcome.Error,
            options.Token);
        break;
    case TokenSource.Stored:
        app.Logger.LogInformation("Using the generated token stored in {Path}", tokenOutcome.Path);
        break;
}

AgentApi.Map(app, options, app.Services.GetRequiredService<SensorHub>(), app.Services.GetRequiredService<TimeProvider>());
app.Run();

/// <summary>Entry point marker so tests can reference the assembly.</summary>
public partial class Program;
