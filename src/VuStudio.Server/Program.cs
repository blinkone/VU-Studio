using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using VuStudio.Core.Config;
using VuStudio.Core.Engine;
using VuStudio.Core.Faces;
using VuStudio.Core.History;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Hardware;
using VuStudio.Rendering;
using VuStudio.Server;
using VuStudio.Server.Api;
using VuStudio.Server.Hosting;
using VuStudio.Server.Live;
using VuStudio.Server.Security;
using VuStudio.Server.Storage;

var runtime = RuntimeOptions.Parse(args);
var paths = AppPaths.Resolve(runtime.DataDirectory);
var configStore = new ConfigStore(paths.Config, NullLogger<ConfigStore>.Instance);
var web = configStore.Current.Web;
var port = runtime.Port ?? web.Port;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Logging.ClearProviders();
if (!runtime.IsService)
{
    builder.Logging.AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss ";
    });
}

builder.Logging.AddProvider(new FileLoggerProvider(paths.Logs));
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

if (runtime.IsService && OperatingSystem.IsWindows())
{
    builder.Host.UseWindowsService(o => o.ServiceName = AppInfo.ServiceName);
    builder.Services.AddSingleton<IHostLifetime, VuServiceLifetime>();
}

builder.WebHost.ConfigureKestrel(kestrel =>
{
    if (web.AllowLan && web.PasswordHash is not null)
    {
        kestrel.ListenAnyIP(port);
    }
    else
    {
        kestrel.ListenLocalhost(port);
    }
});

builder.Services.ConfigureHttpJsonOptions(o =>
{
    var shared = Json.Create(indented: false);
    o.SerializerOptions.PropertyNamingPolicy = shared.PropertyNamingPolicy;
    o.SerializerOptions.DefaultIgnoreCondition = shared.DefaultIgnoreCondition;
    o.SerializerOptions.NumberHandling = shared.NumberHandling;
    o.SerializerOptions.Encoder = shared.Encoder;
    o.SerializerOptions.ReadCommentHandling = shared.ReadCommentHandling;
    o.SerializerOptions.AllowTrailingCommas = true;
    foreach (var converter in shared.Converters)
    {
        o.SerializerOptions.Converters.Add(converter);
    }
});

var services = builder.Services;
services.AddSingleton(TimeProvider.System);
services.AddSingleton(runtime);
services.AddSingleton(paths);
services.AddSingleton(configStore);
services.AddHttpClient();
services.AddSingleton<SensorHub>();
services.AddSingleton(_ => new VuStudio.Sources.Push.PushGateway());

var providerAssemblies = new[] { "VuStudio.Sources", "VuStudio.Sources.Windows" }
    .Select(name =>
    {
        try
        {
            return Assembly.Load(new AssemblyName(name));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    })
    .OfType<Assembly>()
    .ToArray();
foreach (var factory in ProviderCatalog.Discover(providerAssemblies))
{
    services.AddSingleton(factory);
}

services.AddSingleton<ProviderManager>();
services.AddSingleton<SqliteHistoryStore>(_ => new SqliteHistoryStore(paths.History));
services.AddSingleton<IHistoryPersistence>(sp => sp.GetRequiredService<SqliteHistoryStore>());
services.AddSingleton<HistoryRecorder>();
services.AddSingleton<IAssetStore>(_ => new FileAssetStore(paths.Assets));
services.AddSingleton<IFaceRenderer>(sp => new FaceRenderer(sp.GetRequiredService<IAssetStore>()));
services.AddSingleton(sp => new DialController(new DialControllerOptions(), sp.GetRequiredService<ILogger<DialController>>()));
services.AddSingleton<IHardwareHost, DialControllerHost>();
services.AddSingleton<IDialOutput>(sp => sp.GetRequiredService<IHardwareHost>().Output);
services.AddSingleton<NotificationHub>();
services.AddSingleton<INotificationSink>(sp => sp.GetRequiredService<NotificationHub>());
services.AddSingleton<DialEngine>();
services.AddSingleton<SessionState>();
services.AddSingleton<SessionChannel>();
services.AddSingleton<LiveHub>();
services.AddSingleton<StateBuilder>();
services.AddSingleton<ConfigService>();
services.AddSingleton<AuthSessions>();
services.AddHostedService<VuStudioHost>();
services.AddHostedService<StarterBinder>();

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseMiddleware<SecurityMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapVuStudioApi();

var index = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot"), "index.html");
if (File.Exists(index))
{
    app.MapFallbackToFile("index.html");
}
else
{
    app.MapGet("/", () => Results.Content(
        "<!doctype html><title>VU Studio</title><body style=\"font-family:sans-serif\"><h1>VU Studio</h1>" +
        "<p>The service is running, but the web UI has not been built. Run <code>npm run build</code> in <code>web/</code>.</p>",
        "text/html"));
}

app.Lifetime.ApplicationStarted.Register(() =>
    app.Logger.LogInformation("Listening on http://{Host}:{Port}", web.AllowLan ? "0.0.0.0" : "localhost", port));

app.Run();
