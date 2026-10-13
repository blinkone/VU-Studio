using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using VuStudio.Core.Config;
using VuStudio.Core.Engine;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Server.Live;
using VuStudio.Session;
using VuStudio.Sources.Script;

namespace VuStudio.Tests.Session;

/// <summary>The tray client against the service's real <see cref="SessionChannel"/>: proves the wire format matches.</summary>
public sealed class SessionChannelIntegrationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vustudio-session-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Tray_client_round_trips_with_the_service_channel()
    {
        var hub = new SensorHub();
        var session = new SessionState(hub, TimeProvider.System);
        var config = new ConfigStore(Path.Combine(_dir, "config.json"), NullLogger<ConfigStore>.Instance);
        config.Update(c =>
        {
            c.Providers.Add(new ProviderInstanceConfig { Id = "my-script", Type = "script", Enabled = true });
            c.Providers.Add(new ProviderInstanceConfig { Id = "off-script", Type = "script", Enabled = false });
        });
        var services = new SessionServices(NullLoggerFactory.Instance);
        await using var manager = new ProviderManager([new ScriptProviderFactory()], hub, services, NullLogger<ProviderManager>.Instance);
        var notifications = new NotificationHub(NullLogger<NotificationHub>.Instance);
        var channel = new SessionChannel(hub, session, config, manager, notifications, NullLogger<SessionChannel>.Instance);

        await using var host = await TestHost.StartAsync(app => app.Map("/api/session", async (HttpContext http) =>
        {
            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            await channel.HandleAsync(socket, http.RequestAborted);
            return Results.Empty;
        }));

        await using var client = new SessionClient(host.SessionUri, new SessionClientOptions { InitialReconnectDelay = TimeSpan.FromMilliseconds(50) });
        var providers = new TaskCompletionSource<IReadOnlyList<ProviderInstanceConfig>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notified = new TaskCompletionSource<SessionNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ProvidersReceived += (_, p) => providers.TrySetResult(p);
        client.NotificationReceived += (_, n) => notified.TrySetResult(n);

        client.Define(SessionSensors.Provider, SessionSensors.All);
        client.Publish(SessionSensors.Provider, SessionSensors.AudioPeak, 33.3);
        client.Publish(SessionSensors.Provider, SessionSensors.ForegroundProcess, "eldenring.exe");
        client.Publish(SessionSensors.Provider, SessionSensors.Accent, "#0078D7");
        client.Publish(SessionSensors.Provider, SessionSensors.Locked, 1);
        client.Define("my-script", [new SensorInfo { Id = "temp", Name = "Temperature", Unit = Units.Celsius }]);
        client.Publish("my-script", "temp", 21.5);
        client.Publish("intruder", "x", 1);
        client.Start();

        // Providers: only enabled user-session instances are sent.
        var list = await providers.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(["my-script"], list.Select(p => p.Id));

        await Wait.UntilAsync(() => hub.GetNumber(WellKnownSensors.AudioPeak) is not null && hub.GetNumber("my-script/temp") is not null, because: "values in the hub");
        Assert.Equal(33.3, hub.GetNumber(WellKnownSensors.AudioPeak));
        Assert.Equal("eldenring.exe", hub.GetText(WellKnownSensors.ForegroundProcess));
        Assert.Equal("#0078D7", hub.GetText(WellKnownSensors.AccentColor));
        Assert.Equal(21.5, hub.GetNumber("my-script/temp"));
        Assert.True(session.IsLocked);
        Assert.Null(hub.Get("intruder/x"));

        // Metadata arrived intact.
        var peak = hub.Get(WellKnownSensors.AudioPeak)!.Info;
        Assert.Equal(SensorCategory.Audio, peak.Category);
        Assert.Equal(Units.Percent, peak.Unit);
        Assert.Equal(100, peak.Max);
        Assert.Equal("waveform", peak.Icon);
        Assert.Equal(SensorValueKind.Color, hub.Get(WellKnownSensors.AccentColor)!.Info.ValueKind);

        // Every id the engine relies on is defined by the tray (except 'locked', owned by the service).
        foreach (var id in new[]
                 {
                     WellKnownSensors.SessionIdleSeconds, WellKnownSensors.ForegroundProcess, WellKnownSensors.AccentColor, WellKnownSensors.AlbumColor,
                     WellKnownSensors.MediaTitle, WellKnownSensors.MediaArtist, WellKnownSensors.MediaPlaying, WellKnownSensors.MediaProgress,
                     WellKnownSensors.AudioPeak, WellKnownSensors.AudioPeakLeft, WellKnownSensors.AudioPeakRight, WellKnownSensors.AudioVolume,
                 })
        {
            Assert.NotNull(hub.Get(id));
        }

        Assert.Equal("Workstation locked", hub.Get(WellKnownSensors.SessionLocked)!.Info.Name);

        // Invalidation.
        client.Invalidate(SessionSensors.Provider, SessionSensors.AudioPeak);
        await Wait.UntilAsync(() => hub.GetNumber(WellKnownSensors.AudioPeak) is null, because: "invalidated");

        // Notifications flow back to the tray.
        notifications.Notify(new EngineNotification("Alarm", "GPU 90 °C", "alert", 2));
        var n = await notified.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(new SessionNotification("Alarm", "GPU 90 °C", "alert"), n);
    }
    [Fact]
    public async Task Remove_status_and_disabling_reach_the_service()
    {
        var hub = new SensorHub();
        var session = new SessionState(hub, TimeProvider.System);
        var config = new ConfigStore(Path.Combine(_dir, "config.json"), NullLogger<ConfigStore>.Instance);
        config.Update(c => c.Providers.Add(new ProviderInstanceConfig { Id = "my-script", Type = "script", Enabled = true }));
        var services = new SessionServices(NullLoggerFactory.Instance);
        await using var manager = new ProviderManager([new ScriptProviderFactory()], hub, services, NullLogger<ProviderManager>.Instance);
        await manager.ApplyAsync(config.Current.Providers, TestContext.Current.CancellationToken);
        var notifications = new NotificationHub(NullLogger<NotificationHub>.Instance);
        var channel = new SessionChannel(hub, session, config, manager, notifications, NullLogger<SessionChannel>.Instance);

        await using var host = await TestHost.StartAsync(app => app.Map("/api/session", async (HttpContext http) =>
        {
            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            await channel.HandleAsync(socket, http.RequestAborted);
            return Results.Empty;
        }));

        await using var client = new SessionClient(host.SessionUri, new SessionClientOptions { InitialReconnectDelay = TimeSpan.FromMilliseconds(50) });
        client.Define("my-script", [new SensorInfo { Id = "a", Name = "A" }, new SensorInfo { Id = "b", Name = "B" }]);
        client.Publish("my-script", "a", 1);
        client.Publish("my-script", "b", 2);
        client.Start();
        await Wait.UntilAsync(() => hub.GetNumber("my-script/a") is not null && hub.GetNumber("my-script/b") is not null, because: "values in the hub");

        // "remove" drops the sensor from the catalog; "status" lands in the provider status list.
        client.Remove("my-script", "a");
        client.ReportStatus("my-script", ProviderHealth.Degraded, "exit code 2");
        await Wait.UntilAsync(() => hub.Get("my-script/a") is null, because: "removed");
        await Wait.UntilAsync(
            () => manager.GetStatus().Single(s => s.Id == "my-script") is { Health: ProviderHealth.Degraded, Message: "exit code 2" },
            because: "status reported");

        // The tray can never redefine or remove the service-owned lock sensor.
        client.Remove(SessionSensors.Provider, "locked");
        client.Publish(SessionSensors.Provider, SessionSensors.AudioPeak, 5);
        await Wait.UntilAsync(() => hub.GetNumber(WellKnownSensors.AudioPeak) is not null, because: "later message processed");
        Assert.NotNull(hub.Get(WellKnownSensors.SessionLocked));

        // Disabling the provider in the config drops its remaining sensors.
        config.Update(c => c.Providers.Single(p => p.Id == "my-script").Enabled = false);
        await manager.ApplyAsync(config.Current.Providers, TestContext.Current.CancellationToken);
        Assert.Null(hub.Get("my-script/b"));
    }
}
