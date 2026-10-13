using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace VuStudio.Session.Logging;

/// <summary>
/// Minimal <see cref="ILoggerFactory"/> writing daily files <c>{prefix}-yyyyMMdd.log</c> (7 kept) on a
/// background task, so logging never blocks the UI or the audio thread. Used by the desktop app for
/// <c>%LocalAppData%\VU Studio\logs\tray-*.log</c>.
/// </summary>
public sealed class FileLoggerFactory : ILoggerFactory
{
    private const int RetainedFiles = 7;

    private readonly string _directory;
    private readonly string _prefix;
    private readonly LogLevel _minimum;
    private readonly Func<DateTime> _now;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly Channel<(DateTime At, string Line)> _queue = Channel.CreateBounded<(DateTime, string)>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });

    private readonly Task _writer;

    public FileLoggerFactory(string directory, string prefix, LogLevel minimum = LogLevel.Information, Func<DateTime>? now = null)
    {
        _directory = directory;
        _prefix = prefix;
        _minimum = minimum;
        _now = now ?? (() => DateTime.Now);
        Directory.CreateDirectory(directory);
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    public void AddProvider(ILoggerProvider provider)
    {
        // Single sink by design.
    }

    /// <summary>Waits until everything queued so far is on disk (tests, shutdown).</summary>
    public void Dispose()
    {
        _queue.Writer.TryComplete();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }

    private void Enqueue(string line) => _queue.Writer.TryWrite((_now(), line));

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        var day = DateOnly.MinValue;
        try
        {
            await foreach (var (at, line) in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    var today = DateOnly.FromDateTime(at);
                    if (writer is null || today != day)
                    {
                        if (writer is not null)
                        {
                            await writer.DisposeAsync().ConfigureAwait(false);
                        }

                        day = today;
                        var path = Path.Combine(_directory, $"{_prefix}-{today:yyyyMMdd}.log");
                        writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false));
                        Prune();
                    }

                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                    if (_queue.Reader.Count == 0)
                    {
                        await writer.FlushAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Disk full or file locked: logging must never take the app down. Retry with a new file next time.
                    writer = null;
                }
            }
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void Prune()
    {
        try
        {
            foreach (var old in Directory.GetFiles(_directory, $"{_prefix}-*.log").Order(StringComparer.Ordinal).SkipLast(RetainedFiles))
            {
                File.Delete(old);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class FileLogger(FileLoggerFactory owner, string category) : ILogger
    {
        private readonly string _category = category[(category.LastIndexOf('.') + 1)..];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= owner._minimum && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var level = logLevel switch
            {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                _ => "CRT",
            };
            var line = $"{owner._now():yyyy-MM-dd HH:mm:ss.fff} {level} {_category}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            owner.Enqueue(line);
        }
    }
}
