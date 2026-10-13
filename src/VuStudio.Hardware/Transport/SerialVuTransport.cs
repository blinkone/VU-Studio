using System.IO.Ports;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VuStudio.Hardware.Transport;

/// <summary>
/// The VU1 hub on a USB serial port: 115200 baud, 8N1, no handshake.
/// A dedicated background thread reads the port and splits lines into a queue, so serial reads never block a
/// thread-pool thread. It only reads bytes the driver reports as waiting, under the same lock as
/// <see cref="DiscardInput"/>: on Windows <c>DiscardInBuffer</c> (PurgeComm with PURGE_RXABORT) aborts a pending
/// read, which surfaced as "The operation was canceled" and dropped the link before every command.
/// Unplugging is detected from I/O errors and by checking that the port still exists.
/// </summary>
public sealed class SerialVuTransport : IVuTransport
{
    public const int BaudRate = 115200;

    private const int Created = 0;
    private const int Open = 1;
    private const int Closed = 2;

    /// <summary>Read timeout of the port, a safety net only: the reader never reads more than is waiting.</summary>
    private static readonly TimeSpan ReadPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Pause between checks for waiting bytes while the hub is silent; well below a reply's round trip.</summary>
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(2);
    private static readonly TimeSpan PresenceCheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReaderStopTimeout = TimeSpan.FromSeconds(2);

    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly LineQueue _lines;
    private readonly Action<string> _addLine;
    private readonly VuLineSplitter _splitter = new();
    private readonly Lock _receiveLock = new();
    private readonly ManualResetEventSlim _stopping = new();
    private readonly TaskCompletionSource _readerExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SerialPort? _port;
    private int _state;
    private int _lost;

    public SerialVuTransport(string portName, ILogger? logger = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        Name = portName;
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _lines = new LineQueue(_time);
        _addLine = _lines.Add;
    }

    public string Name { get; }

    public bool IsOpen => Volatile.Read(ref _state) == Open && Volatile.Read(ref _lost) == 0;

    public event Action<Exception>? Disconnected;

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _state) != Created)
        {
            throw new InvalidOperationException("A transport can only be opened once.");
        }

        var port = new SerialPort(Name, BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,

            // VU-Server (pyserial) asserts DTR and RTS when it opens the port; do the same.
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = (int)ReadPollInterval.TotalMilliseconds,
            WriteTimeout = (int)WriteTimeout.TotalMilliseconds,
            Encoding = Encoding.ASCII,
        };

        try
        {
            port.Open();
            port.DiscardInBuffer();
            port.DiscardOutBuffer();
        }
        catch
        {
            port.Dispose();
            throw;
        }

        if (Interlocked.CompareExchange(ref _state, Open, Created) != Created)
        {
            port.Dispose();
            throw new InvalidOperationException("A transport can only be opened once.");
        }

        _port = port;
        var reader = new Thread(ReadLoop) { IsBackground = true, Name = $"VU1 serial reader ({Name})" };
        reader.Start(port);
        _logger.LogDebug("Opened serial port {Port}", Name);
        return Task.CompletedTask;
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(line);
        var port = ActivePort();
        var bytes = Encoding.ASCII.GetBytes(line + "\r\n");

        using var timeout = new CancellationTokenSource(WriteTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await port.BaseStream.WriteAsync(bytes, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Lose(new IOException($"Writing to {Name} timed out."));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException or TimeoutException)
        {
            throw Lose(ex as IOException ?? new IOException($"Writing to {Name} failed: {ex.Message}", ex));
        }
    }

    public Task<string?> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _state) == Created)
        {
            throw new InvalidOperationException("The transport is not open.");
        }

        return _lines.ReadAsync(timeout, Name, cancellationToken);
    }

    public void DiscardInput()
    {
        lock (_receiveLock)
        {
            _splitter.Reset();
            _lines.Clear();
            if (_port is { } port && IsOpen)
            {
                try
                {
                    port.DiscardInBuffer();
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    // A broken port surfaces on the next read or write.
                    _logger.LogDebug(ex, "Discarding input on {Port} failed", Name);
                }
            }
        }
    }

    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _state, Closed) != Open)
        {
            _lines.Complete(null);
            return;
        }

        _stopping.Set();
        try
        {
            _port?.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Closing {Port} failed", Name);
        }

        try
        {
            await _readerExited.Task.WaitAsync(ReaderStopTimeout, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("The reader thread of {Port} did not stop within {Timeout}", Name, ReaderStopTimeout);
        }

        _lines.Complete(null);
        _logger.LogDebug("Closed serial port {Port}", Name);
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    private SerialPort ActivePort()
    {
        var state = Volatile.Read(ref _state);
        if (state == Created || _port is null)
        {
            throw new InvalidOperationException("The transport is not open.");
        }

        if (state == Closed || Volatile.Read(ref _lost) != 0)
        {
            throw new IOException($"The connection to {Name} is closed.");
        }

        return _port;
    }

    private void ReadLoop(object? state)
    {
        var port = (SerialPort)state!;
        var buffer = new byte[4096];
        var lastPresenceCheck = _time.GetTimestamp();
        try
        {
            var stream = port.BaseStream;
            while (!_stopping.IsSet)
            {
                var received = false;
                lock (_receiveLock)
                {
                    var waiting = port.BytesToRead;
                    if (waiting > 0)
                    {
                        int count;
                        try
                        {
                            // Never blocks: the bytes are already in the driver's buffer.
                            count = stream.Read(buffer, 0, Math.Min(waiting, buffer.Length));
                        }
                        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException && !_stopping.IsSet)
                        {
                            count = 0;
                        }

                        if (count > 0)
                        {
                            _splitter.Feed(buffer.AsSpan(0, count), _addLine);
                            received = true;
                        }
                    }
                }

                if (received)
                {
                    continue;
                }

                _stopping.Wait(IdlePollInterval);

                if (_time.GetElapsedTime(lastPresenceCheck) >= PresenceCheckInterval)
                {
                    lastPresenceCheck = _time.GetTimestamp();
                    if (!PortExists(Name))
                    {
                        throw new IOException($"{Name} is no longer present.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Any failure ends the link; never let it escape a background thread.
            if (!_stopping.IsSet)
            {
                Lose(ex as IOException ?? new IOException($"Reading from {Name} failed: {ex.Message}", ex));
            }
        }
        finally
        {
            _readerExited.TrySetResult();
        }
    }

    private IOException Lose(IOException error)
    {
        if (_stopping.IsSet || Interlocked.Exchange(ref _lost, 1) != 0)
        {
            return error;
        }

        _logger.LogDebug(error, "Lost the connection to {Port}", Name);
        _lines.Complete(error);
        if (Disconnected is { } handlers)
        {
            foreach (var handler in handlers.GetInvocationList().Cast<Action<Exception>>())
            {
                try
                {
                    handler(error);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "A Disconnected handler of {Port} failed", Name);
                }
            }
        }

        return error;
    }

    private static bool PortExists(string name)
    {
        try
        {
            return !OperatingSystem.IsWindows() && Path.IsPathRooted(name)
                ? File.Exists(name)
                : SerialPort.GetPortNames().Contains(name, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Cannot tell; let real I/O errors decide.
            return true;
        }
    }
}
