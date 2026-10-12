using System.Threading.Channels;

namespace VuStudio.Hardware.Transport;

/// <summary>
/// Received lines waiting to be read. Bounded: when nobody reads, the oldest lines are dropped, which is
/// harmless because the client discards stale input before every command anyway.
/// </summary>
internal sealed class LineQueue
{
    private readonly Channel<string> _channel;
    private readonly TimeProvider _time;
    private Exception? _failure;

    public LineQueue(TimeProvider time, int capacity = 256)
    {
        _time = time;
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    }

    public void Add(string line) => _channel.Writer.TryWrite(line);

    public void Clear()
    {
        while (_channel.Reader.TryRead(out _))
        {
        }
    }

    /// <summary>Stops accepting lines. Lines already queued can still be read.</summary>
    public void Complete(Exception? failure)
    {
        if (failure is not null)
        {
            Interlocked.CompareExchange(ref _failure, failure, null);
        }

        _channel.Writer.TryComplete();
    }

    /// <summary>Next line, null on timeout; <see cref="IOException"/> once completed and empty.</summary>
    public async Task<string?> ReadAsync(TimeSpan timeout, string name, CancellationToken cancellationToken)
    {
        var reader = _channel.Reader;
        if (reader.TryRead(out var line))
        {
            return line;
        }

        if (reader.Completion.IsCompleted)
        {
            throw Closed(name);
        }

        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            return null;
        }

        using var timeoutSource = timeout == Timeout.InfiniteTimeSpan ? null : new CancellationTokenSource(timeout, _time);
        using var linked = timeoutSource is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            while (await reader.WaitToReadAsync(linked?.Token ?? cancellationToken).ConfigureAwait(false))
            {
                if (reader.TryRead(out line))
                {
                    return line;
                }
            }
        }
        catch (OperationCanceledException) when (timeoutSource?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        throw Closed(name);
    }

    private IOException Closed(string name)
    {
        var failure = Volatile.Read(ref _failure);
        return failure is null
            ? new IOException($"The connection to {name} is closed.")
            : new IOException($"The connection to {name} was lost: {failure.Message}", failure);
    }
}
