using System.Reflection;

namespace VuStudio.Session;

/// <summary>Tuning knobs of <see cref="SessionClient"/>. The defaults match the service's expectations.</summary>
public sealed class SessionClientOptions
{
    /// <summary>Minimum time between two flushes: at most ~20 batches per second.</summary>
    public TimeSpan MinSendInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>First reconnect delay; doubles up to <see cref="MaxReconnectDelay"/>.</summary>
    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A connection that lasted at least this long resets the reconnect backoff.</summary>
    public TimeSpan StableConnection { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>WebSocket ping interval; a missing pong after the same time aborts the connection.</summary>
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Largest number of sensors or values per message (the service accepts messages up to 1 MB).</summary>
    public int MaxItemsPerMessage { get; init; } = 200;

    /// <summary>Sent in <c>hello</c>.</summary>
    public string User { get; init; } = Environment.UserName;

    /// <summary>Sent in <c>hello</c>.</summary>
    public string Machine { get; init; } = Environment.MachineName;

    /// <summary>Sent in <c>hello</c>.</summary>
    public string Version { get; init; } = DefaultVersion();

    private static string DefaultVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(SessionClientOptions).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }
}
