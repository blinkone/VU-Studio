using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Session;

namespace VuStudio.Tests.Session;

public class SessionClientTests
{
    private static readonly SessionClientOptions Fast = new()
    {
        InitialReconnectDelay = TimeSpan.FromMilliseconds(50),
        MaxReconnectDelay = TimeSpan.FromMilliseconds(200),
        User = "alice",
        Machine = "PC",
        Version = "9.9.9",
    };

    private static SensorInfo Info(string id, string name = "x") => new() { Id = id, Name = name, Unit = Units.Percent, Min = 0, Max = 100 };

    [Fact]
    public async Task Sends_hello_first_without_an_origin_header()
    {
        var server = new RecordingSessionServer();
        await using var host = await TestHost.StartAsync(server.Map);
        await using var client = new SessionClient(host.SessionUri, Fast);
        client.Start();

        await Wait.UntilAsync(() => server.Messages.Count >= 1, because: "hello");
        var hello = server.Messages[0];
        Assert.Equal("hello", hello.Type);
        Assert.Equal("alice", hello.Json.GetProperty("user").GetString());
        Assert.Equal("PC", hello.Json.GetProperty("machine").GetString());
        Assert.Equal("9.9.9", hello.Json.GetProperty("version").GetString());
        Assert.False(server.LastHeaders!.ContainsKey("Origin"));
        await Wait.UntilAsync(() => client.IsConnected, because: "connected flag");
    }

    [Fact]
    public async Task Defines_are_sent_before_values_with_full_metadata()
    {
        var server = new RecordingSessionServer();
        await using var host = await TestHost.StartAsync(server.Map);
        await using var client = new SessionClient(host.SessionUri, Fast);
        client.Define("session", [Info("audio/peak", "Output peak") with { Category = SensorCategory.Audio, Icon = "waveform", StaleAfterSeconds = 5 }]);
        client.Publish("session", "audio/peak", 42.5);
        client.Start();

        await Wait.UntilAsync(() => server.OfType("values").Count >= 1, because: "values");
        var types = server.Messages.Select(m => m.Type).ToList();
        Assert.True(types.IndexOf("define") < types.IndexOf("values"));

        var define = server.OfType("define")[0].Json;
        Assert.Equal("session", define.GetProperty("provider").GetString());
        var sensor = define.GetProperty("sensors")[0];
        Assert.Equal("audio/peak", sensor.GetProperty("id").GetString());
        Assert.Equal("Output peak", sensor.GetProperty("name").GetString());
        Assert.Equal("audio", sensor.GetProperty("category").GetString());
        Assert.Equal("%", sensor.GetProperty("unit").GetString());
        Assert.Equal(5, sensor.GetProperty("staleAfterSeconds").GetDouble());
        Assert.Equal(42.5, server.LatestValues("session")["audio/peak"].GetDouble());
    }

    [Fact]
    public async Task Values_are_coalesced_to_at_most_about_20_messages_per_second_latest_wins()
    {
        var server = new RecordingSessionServer();
        await using var host = await TestHost.StartAsync(server.Map);
        await using var client = new SessionClient(host.SessionUri, Fast);
        client.Start();
        await Wait.UntilAsync(() => client.IsConnected);
        await Wait.UntilAsync(() => server.Messages.Count >= 1);

        var start = server.Clock.Elapsed;
        for (var i = 1; i <= 2000; i++)
        {
            client.Publish("session", "audio/peak", i);
            client.Publish("session", "audio/peak-left", i / 2.0);
            if (i % 100 == 0)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }

        await Wait.UntilAsync(() => server.LatestValues("session").TryGetValue("audio/peak", out var v) && v.GetDouble() == 2000, because: "latest value");
        var elapsed = server.Clock.Elapsed - start;
        var valueMessages = server.OfType("values");

        // 4000 publishes become a handful of batches: one per 50 ms flush at most (+ slack for timer jitter).
        Assert.InRange(valueMessages.Count, 1, (int)(elapsed.TotalMilliseconds / 50) + 3);
        // Arrival times can bunch under load even when sends are spaced, so check the average gap.
        if (valueMessages.Count > 2)
        {
            var span = valueMessages[^1].At - valueMessages[0].At;
            Assert.True(span / (valueMessages.Count - 1) >= TimeSpan.FromMilliseconds(35), "flushes must be spaced by the send interval on average");
        }

        Assert.Equal(1000, server.LatestValues("session")["audio/peak-left"].GetDouble());
        Assert.All(valueMessages, m => Assert.True(m.Json.GetProperty("values").EnumerateObject().Count() <= 2));
    }

    [Fact]
    public async Task Encodes_numbers_text_and_null_and_normalises_ids()
    {
        var server = new RecordingSessionServer();
        await using var host = await TestHost.StartAsync(server.Map);
        await using var client = new SessionClient(host.SessionUri, Fast);
        client.Publish("session", "session/idle", 12);
        client.Publish("session", "media/title", "Caffè «latte»");
        client.Publish("session", "media/progress", double.NaN);
        client.Publish("session", "media/artist", (string?)null);
        client.Invalidate("session", "game/fps");
        client.Start();

        await Wait.UntilAsync(() => server.LatestValues("session").Count >= 5, because: "five values");
        var values = server.LatestValues("session");
        Assert.Equal(12, values["idle"].GetDouble());
        Assert.Equal("Caffè «latte»", values["media/title"].GetString());
        Assert.Equal(JsonValueKind.Null, values["media/progress"].ValueKind);
        Assert.Equal(JsonValueKind.Null, values["media/artist"].ValueKind);
        Assert.Equal(JsonValueKind.Null, values["game/fps"].ValueKind);
        Assert.False(values.ContainsKey("session/idle"));
    }

    [Fact]
    public async Task Resends_hello_definitions_and_last_values_after_a_reconnect()
    {
        var server = new RecordingSessionServer();
        await using var host = await TestHost.StartAsync(server.Map);
        await using var client = new SessionClient(host.SessionUri, Fast);
        var changes = new List<bool>();
        client.ConnectionChanged += (_, c) =>
        {
            lock (changes)
            {
                changes.Add(c);
            }
        };
        client.Define("session", [Info("audio/volume"), Info("media/title")]);
        client.Define("script-1", [Info("temp")]);
        client.Start();
        client.Publish("session", "audio/volume", 10);
        client.Publish("session", "media/title", "A");
        client.Publish("script-1", "temp", 21.5);
        client.ReportStatus("script-1", ProviderHealth.Ok);
        await Wait.UntilAsync(() => server.LatestValues("session", 1).Count == 2 && server.LatestValues("script-1", 1).Count == 1, because: "first sync");

        client.Publish("session", "audio/volume", 55);
        server.AbortAll();
        await Wait.UntilAsync(() => server.Connections >= 2, because: "reconnect");
        await Wait.UntilAsync(() => server.LatestValues("session", 2).Count == 2 && server.LatestValues("script-1", 2).Count == 1, because: "resync");

        var second = server.Messages.Where(m => m.Connection == 2).ToList();
        Assert.Equal("hello", second[0].Type);
        var defined = server.OfType("define", 2).SelectMany(m => m.Json.GetProperty("sensors").EnumerateArray().Select(s => m.Json.GetProperty("provider").GetString() + "/" + s.GetProperty("id").GetString())).ToHashSet();
        Assert.Equal(["script-1/temp", "session/audio/volume", "session/media/title"], defined.Order());
        Assert.Equal(55, server.LatestValues("session", 2)["audio/volume"].GetDouble());
        Assert.Equal("A", server.LatestValues("session", 2)["media/title"].GetString());
        Assert.Equal(21.5, server.LatestValues("script-1", 2)["temp"].GetDouble());
        Assert.Contains(server.OfType("status", 2), m => m.Json.GetProperty("provider").GetString() == "script-1" && m.Json.GetProperty("health").GetString() == "ok");
        lock (changes)
        {
            Assert.Equal([true, false, true], changes.Take(3));
        }
    }

    [Fact]
    public async Task Connects_once_the_service_comes_up_later()
    {
        var port = FreePort();
        var server = new RecordingSessionServer();
        await using var client = new SessionClient(SessionClient.EndpointFor(port), Fast);
        client.Define("session", [Info("idle")]);
        client.Publish("session", "idle", 3);
        client.Start();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(client.IsConnected);

        await using var host = await TestHost.StartAsync(server.Map, port: port);
        await Wait.UntilAsync(() => server.LatestValues("session").ContainsKey("idle"), because: "late connection");
        Assert.Equal(3, server.LatestValues("session")["idle"].GetDouble());

        // Disconnect before the host stops, or Kestrel waits for the open socket until its shutdown timeout.
        await client.StopAsync();
    }

    [Fact]
    public async Task Raises_notification_and_providers_events()
    {
        var server = new RecordingSessionServer();
        await using var host = await TestHost.StartAsync(server.Map);
        await using var client = new SessionClient(host.SessionUri, Fast);
        var notified = new TaskCompletionSource<SessionNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        var providers = new TaskCompletionSource<IReadOnlyList<ProviderInstanceConfig>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.NotificationReceived += (_, n) => notified.TrySetResult(n);
        client.ProvidersReceived += (_, p) => providers.TrySetResult(p);
        client.Start();
        await Wait.UntilAsync(() => server.Messages.Count >= 1);

        await server.SendAsync(new { type = "notify", title = "CPU hot", message = "95 °C", icon = "alert" });
        await server.SendAsync(new { type = "unknown" });
        await server.SendAsync(new
        {
            type = "providers",
            items = new object[] { new { id = "weather-script", type = "script", name = "Meteo", enabled = true, settings = new { command = "echo 1", interval = 5 } } },
        });

        var n = await notified.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(new SessionNotification("CPU hot", "95 °C", "alert"), n);
        var list = await providers.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var item = Assert.Single(list);
        Assert.Equal("weather-script", item.Id);
        Assert.Equal("script", item.Type);
        Assert.Equal("Meteo", item.Name);
        Assert.Equal("echo 1", item.Settings["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task Handler_exceptions_and_malformed_messages_do_not_break_the_connection()
    {
        var server = new RecordingSessionServer();
        await using var host = await TestHost.StartAsync(server.Map);
        await using var client = new SessionClient(host.SessionUri, Fast);
        var count = 0;
        client.NotificationReceived += (_, _) =>
        {
            Interlocked.Increment(ref count);
            throw new InvalidOperationException("boom");
        };
        client.Start();
        await Wait.UntilAsync(() => server.Messages.Count >= 1);

        await server.SendAsync(new { type = "notify", title = "a", message = "b" });
        await server.SendAsync("not an object");
        await server.SendAsync(new { type = "notify", title = "c", message = "d" });
        await Wait.UntilAsync(() => Volatile.Read(ref count) == 2, because: "both notifications");
        Assert.True(client.IsConnected);
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public async Task Remove_and_clear_invalidate_and_forget_sensors()
    {
        var server = new RecordingSessionServer();
        await using var host = await TestHost.StartAsync(server.Map);
        await using var client = new SessionClient(host.SessionUri, Fast);
        client.Define("script-1", [Info("a"), Info("b")]);
        client.Publish("script-1", "a", 1);
        client.Publish("script-1", "b", 2);
        client.Start();
        await Wait.UntilAsync(() => server.LatestValues("script-1").Count == 2);

        client.Remove("script-1", "a");
        await Wait.UntilAsync(() => server.OfType("remove").Count == 1, because: "remove message");
        Assert.Equal(JsonValueKind.Null, server.LatestValues("script-1")["a"].ValueKind);
        Assert.Equal("a", server.OfType("remove")[0].Json.GetProperty("sensors")[0].GetString());

        client.ClearProvider("script-1");
        await Wait.UntilAsync(() => server.OfType("remove").Count == 2, because: "clear");
        Assert.Equal(JsonValueKind.Null, server.LatestValues("script-1")["b"].ValueKind);

        // Forgotten sensors are not re-sent after a reconnect.
        client.Publish("session", "idle", 1);
        server.AbortAll();
        await Wait.UntilAsync(() => server.LatestValues("session", 2).ContainsKey("idle"), because: "resync");
        Assert.Empty(server.OfType("define", 2));
        Assert.Empty(server.LatestValues("script-1", 2));
    }

    [Fact]
    public async Task SetEndpoint_moves_to_the_new_port()
    {
        var first = new RecordingSessionServer();
        var second = new RecordingSessionServer();
        await using var host1 = await TestHost.StartAsync(first.Map);
        await using var host2 = await TestHost.StartAsync(second.Map);
        await using var client = new SessionClient(host1.SessionUri, Fast);
        client.Publish("session", "idle", 7);
        client.Start();
        await Wait.UntilAsync(() => first.LatestValues("session").ContainsKey("idle"));

        client.SetEndpoint(host2.SessionUri);
        await Wait.UntilAsync(() => second.LatestValues("session").ContainsKey("idle"), because: "second endpoint");
        Assert.Equal(host2.SessionUri, client.Endpoint);
    }

    [Fact]
    public async Task Stop_closes_the_socket_politely()
    {
        var server = new RecordingSessionServer();
        await using var host = await TestHost.StartAsync(server.Map);
        var client = new SessionClient(host.SessionUri, Fast);
        client.Start();
        await Wait.UntilAsync(() => client.IsConnected);

        await client.StopAsync();
        Assert.False(client.IsConnected);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public void Rejects_non_websocket_endpoints() =>
        Assert.Throws<ArgumentException>(() => new SessionClient(new Uri("http://127.0.0.1:5341/api/session")));

    [Fact]
    public void Splits_large_batches()
    {
        var client = new SessionClient(SessionClient.EndpointFor(1), new SessionClientOptions { MaxItemsPerMessage = 10 });
        client.Define("session", Enumerable.Range(0, 25).Select(i => Info($"s{i}")));
        for (var i = 0; i < 25; i++)
        {
            client.Publish("session", $"s{i}", i);
        }

        var messages = client.TakePendingMessages().Select(m => JsonDocument.Parse(m).RootElement.GetProperty("type").GetString()).ToList();
        Assert.Equal(["define", "define", "define", "values", "values", "values"], messages);
        Assert.Empty(client.TakePendingMessages());
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
