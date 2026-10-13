using System.Threading.Channels;

namespace VuStudio.Hardware.Transport;

/// <summary>
/// A connection to a <see cref="SimulatedHub"/>. Replies are delivered in order after the hub's
/// <see cref="SimulatedHub.Latency"/>; <see cref="DiscardInput"/> cannot recall replies still "on the wire",
/// just like a real serial port. Unplugging the hub raises <see cref="Disconnected"/>.
/// </summary>
public sealed class SimulatedVuTransport : IVuTransport
{
    private const int Created = 0;
    private const int Open = 1;
    private const int Closed = 2;

    private readonly SimulatedHub _hub;
    private readonly TimeProvider _time;
    private readonly LineQueue _lines;
    private readonly Channel<(long Due, IReadOnlyList<string> Lines)> _inFlight = Channel.CreateUnbounded<(long, IReadOnlyList<string>)>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _stop = new();
    private Task _delivery = Task.CompletedTask;
    private int _pending;
    private int _state;
    private int _lost;

    public SimulatedVuTransport(SimulatedHub hub, TimeProvider? time = null)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _time = time ?? TimeProvider.System;
        _lines = new LineQueue(_time);
    }

    public string Name => "simulator";

    public bool IsOpen => Volatile.Read(ref _state) == Open && Volatile.Read(ref _lost) == 0;

    public event Action<Exception>? Disconnected;

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _state, Open, Created) != Created)
        {
            throw new InvalidOperationException("A transport can only be opened once.");
        }

        if (!_hub.Attach(this))
        {
            Volatile.Write(ref _state, Closed);
            throw new IOException("The simulated hub is unplugged.");
        }

        _delivery = DeliverAsync(_stop.Token);
        return Task.CompletedTask;
    }

    public Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(line);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureUsable();

        var replies = _hub.Process(line);
        if (replies.Count == 0)
        {
            return Task.CompletedTask;
        }

        var latency = _hub.Latency;
        if (latency <= TimeSpan.Zero && Volatile.Read(ref _pending) == 0)
        {
            foreach (var reply in replies)
            {
                _lines.Add(reply);
            }
        }
        else
        {
            Interlocked.Increment(ref _pending);
            var due = _time.GetTimestamp() + (long)(latency.TotalSeconds * _time.TimestampFrequency);
            _inFlight.Writer.TryWrite((due, replies));
        }

        return Task.CompletedTask;
    }

    public Task<string?> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _state) == Created)
        {
            throw new InvalidOperationException("The transport is not open.");
        }

        return _lines.ReadAsync(timeout, Name, cancellationToken);
    }

    public void DiscardInput() => _lines.Clear();

    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _state, Closed) == Open)
        {
            _hub.Detach(this);
            _inFlight.Writer.TryComplete();
            await _stop.CancelAsync().ConfigureAwait(false);
            await _delivery.ConfigureAwait(false);
        }

        _lines.Complete(null);
        _stop.Dispose();
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    internal void OnUnplugged()
    {
        if (Volatile.Read(ref _state) != Open || Interlocked.Exchange(ref _lost, 1) != 0)
        {
            return;
        }

        var error = new IOException("The simulated hub was unplugged.");
        _lines.Complete(error);
        Disconnected?.Invoke(error);
    }

    private void EnsureUsable()
    {
        switch (Volatile.Read(ref _state))
        {
            case Created:
                throw new InvalidOperationException("The transport is not open.");
            case Closed:
                throw new IOException("The connection to the simulator is closed.");
        }

        if (Volatile.Read(ref _lost) != 0 || !_hub.IsPluggedIn)
        {
            throw new IOException("The simulated hub is unplugged.");
        }
    }

    private async Task DeliverAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var (due, lines) in _inFlight.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var wait = due - _time.GetTimestamp();
                if (wait > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds((double)wait / _time.TimestampFrequency), _time, cancellationToken).ConfigureAwait(false);
                }

                foreach (var line in lines)
                {
                    _lines.Add(line);
                }

                Interlocked.Decrement(ref _pending);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
