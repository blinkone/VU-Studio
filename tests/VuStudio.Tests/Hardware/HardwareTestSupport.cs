using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VuStudio.Core.Faces;
using VuStudio.Hardware.Discovery;

namespace VuStudio.Tests.Hardware;

internal static class Eventually
{
    /// <summary>Polls <paramref name="condition"/> until it holds; fails the test after <paramref name="timeout"/> (default 10 s).</summary>
    public static async Task HoldsAsync(Func<bool> condition, string description, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > limit)
            {
                Assert.Fail($"Timed out after {limit.TotalSeconds:0.#} s waiting until {description}.");
            }

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>Writes to the test output and remembers warnings and errors.</summary>
internal sealed class TestLogger<T> : ILogger<T>
{
    private readonly ITestOutputHelper? _output = TestContext.Current.TestOutputHelper;
    private readonly ConcurrentQueue<string> _problems = new();

    public IReadOnlyCollection<string> Errors => [.. _problems.Where(p => p.StartsWith("Error", StringComparison.Ordinal) || p.StartsWith("Critical", StringComparison.Ordinal))];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var line = $"{logLevel}: {formatter(state, exception)}{(exception is null ? "" : $" [{exception.GetType().Name}: {exception.Message}]")}";
        if (logLevel >= LogLevel.Warning)
        {
            _problems.Enqueue(line);
        }

        try
        {
            _output?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
        }
        catch (InvalidOperationException)
        {
            // Background work may log after the test has finished.
        }
    }
}

internal sealed class FixedPortLocator(params HubPortCandidate[] candidates) : IHubPortLocator
{
    public IReadOnlyList<HubPortCandidate> FindCandidates() => candidates;
}

internal static class TestFaces
{
    /// <summary>A face with features in every frame: border, diagonal, a block and a checkerboard.</summary>
    public static MonoBitmap Pattern(int seed = 0)
    {
        var face = new MonoBitmap();
        for (var x = 0; x < MonoBitmap.DialWidth; x++)
        {
            face[x, 0] = false;
            face[x, MonoBitmap.DialHeight - 1] = false;
            face[x, (x + seed) % MonoBitmap.DialHeight] = false;
        }

        for (var y = 0; y < MonoBitmap.DialHeight; y++)
        {
            face[0, y] = false;
            face[MonoBitmap.DialWidth - 1, y] = false;
        }

        for (var x = 120; x < 160; x++)
        {
            for (var y = 30; y < 70; y++)
            {
                face[x, y] = (x + y) % 2 == 0;
            }
        }

        return face;
    }
}
