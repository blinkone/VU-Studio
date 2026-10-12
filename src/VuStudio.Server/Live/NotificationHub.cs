using System.Collections.Concurrent;
using System.Threading.Channels;
using VuStudio.Core.Engine;

namespace VuStudio.Server.Live;

/// <summary>Fans engine notifications out to every connected UI and tray client.</summary>
public sealed class NotificationHub : INotificationSink
{
    private readonly ConcurrentDictionary<Guid, Channel<EngineNotification>> _subscribers = new();
    private readonly ILogger<NotificationHub> _logger;

    public NotificationHub(ILogger<NotificationHub> logger)
    {
        _logger = logger;
    }

    public void Notify(EngineNotification notification)
    {
        _logger.LogInformation("Notification: {Title} — {Message}", notification.Title, notification.Message);
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(notification);
        }
    }

    /// <summary>Registers a subscriber; dispose the returned handle to unsubscribe.</summary>
    public Subscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<EngineNotification>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest });
        _subscribers[id] = channel;
        return new Subscription(channel.Reader, () => _subscribers.TryRemove(id, out _));
    }

    public sealed class Subscription(ChannelReader<EngineNotification> reader, Action dispose) : IDisposable
    {
        public ChannelReader<EngineNotification> Reader { get; } = reader;

        public void Dispose() => dispose();
    }
}
