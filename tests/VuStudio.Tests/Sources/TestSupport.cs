using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;

namespace VuStudio.Tests.Sources;

/// <summary>Records every health report a provider makes.</summary>
internal sealed class RecordingStatus : IProviderStatus
{
    private readonly List<(ProviderHealth Health, string? Message)> _reports = [];

    public IReadOnlyList<(ProviderHealth Health, string? Message)> Reports
    {
        get
        {
            lock (_reports)
            {
                return _reports.ToList();
            }
        }
    }

    public (ProviderHealth Health, string? Message)? Last => Reports.Count == 0 ? null : Reports[^1];

    public void Report(ProviderHealth health, string? message = null)
    {
        lock (_reports)
        {
            _reports.Add((health, message));
        }
    }
}

/// <summary>A clock that only moves when told to, with a configurable local time zone.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly TimeZoneInfo _zone;
    private DateTimeOffset _utcNow;
    private long _ticks;

    public ManualTimeProvider(DateTimeOffset utcNow, TimeZoneInfo? zone = null)
    {
        _utcNow = utcNow;
        _zone = zone ?? TimeZoneInfo.Utc;
    }

    public override TimeZoneInfo LocalTimeZone => _zone;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override long GetTimestamp() => _ticks;

    public void Advance(TimeSpan by)
    {
        _utcNow += by;
        _ticks += by.Ticks;
    }

    public static TimeZoneInfo FixedZone(double hours) =>
        TimeZoneInfo.CreateCustomTimeZone("test" + hours, TimeSpan.FromHours(hours), "test", "test");
}

internal static class TestServices
{
    public static IServiceProvider Empty() => new ServiceCollection().BuildServiceProvider();

    public static IServiceProvider With(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        return services.BuildServiceProvider();
    }
}

internal static class TestConfig
{
    /// <summary>A provider instance config whose settings are given as a JSON object literal.</summary>
    public static ProviderInstanceConfig For(string type, string settingsJson = "{}", string? id = null) => new()
    {
        Id = id ?? type,
        Type = type,
        Settings = JsonNode.Parse(settingsJson)!.AsObject(),
    };
}

/// <summary>
/// Drives a <see cref="PollingSensorProvider"/> one step at a time, without timers, so that tests can
/// control the clock. It calls the protected <c>InitializeAsync</c> and <c>PollAsync</c> by reflection.
/// </summary>
internal static class ProviderDriver
{
    private static readonly System.Reflection.BindingFlags Flags =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    public static Task InitializeAsync(ISensorProvider provider, ISensorSink sink) =>
        Invoke(provider, "InitializeAsync", sink);

    public static Task PollAsync(ISensorProvider provider, ISensorSink sink) =>
        Invoke(provider, "PollAsync", sink);

    private static Task Invoke(ISensorProvider provider, string method, ISensorSink sink)
    {
        Assert.IsAssignableFrom<PollingSensorProvider>(provider);
        var info = typeof(PollingSensorProvider).GetMethod(method, Flags)
                   ?? throw new InvalidOperationException($"PollingSensorProvider.{method} not found.");
        return (Task)info.Invoke(provider, [sink, CancellationToken.None])!;
    }
}

internal static class ProviderRunner
{
    /// <summary>
    /// Runs the provider against <paramref name="hub"/> until <paramref name="condition"/> holds, then cancels it.
    /// Fails the test when the condition does not hold within <paramref name="timeout"/>.
    /// </summary>
    public static async Task RunUntilAsync(
        ISensorProvider provider,
        SensorHub hub,
        Func<bool> condition,
        TimeSpan? timeout = null,
        RecordingStatus? status = null,
        string? providerId = null)
    {
        status ??= new RecordingStatus();
        var limit = timeout ?? TimeSpan.FromSeconds(20);
        using var cts = new CancellationTokenSource();
        var run = provider.RunAsync(hub.CreateSink(providerId ?? provider.Id), status, cts.Token);
        var clock = Stopwatch.StartNew();
        try
        {
            while (!condition())
            {
                if (run.IsCompleted)
                {
                    await run; // surfaces a crash instead of waiting for the timeout
                    break;
                }

                Assert.True(clock.Elapsed < limit, "Timed out waiting for the provider to publish.");
                await Task.Delay(10);
            }
        }
        finally
        {
            await cts.CancelAsync();
            try
            {
                await run;
            }
            catch (OperationCanceledException)
            {
            }

            await provider.DisposeAsync();
        }
    }
}

/// <summary>A scratch directory under the test output folder that is deleted on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory(string prefix = "tmp")
    {
        Path = System.IO.Path.Combine(AppContext.BaseDirectory, "scratch", prefix + "-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Writes a file (creating folders), using forward slashes in <paramref name="relativePath"/>.</summary>
    public string Write(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void CreateDirectory(string relativePath) =>
        Directory.CreateDirectory(System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar)));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
