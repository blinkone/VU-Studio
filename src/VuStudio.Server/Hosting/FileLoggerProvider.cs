using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace VuStudio.Server.Hosting;

/// <summary>
/// Minimal daily-rolling file logger (<c>vustudio-yyyyMMdd.log</c>, 7 files kept). Writes happen on a
/// background task so logging never blocks the engine or the serial worker.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int RetainedFiles = 7;

    private readonly string _directory;
    private readonly LogLevel _minimum;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });

    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly Task _writer;

    public FileLoggerProvider(string directory, LogLevel minimum = LogLevel.Information)
    {
        _directory = directory;
        _minimum = minimum;
        Directory.CreateDirectory(directory);
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

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

    private void Enqueue(string line) => _queue.Writer.TryWrite(line);

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        var currentDay = DateOnly.MinValue;
        try
        {
            await foreach (var line in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (writer is null || today != currentDay)
                {
                    if (writer is not null)
                    {
                        await writer.DisposeAsync().ConfigureAwait(false);
                    }

                    currentDay = today;
                    var path = Path.Combine(_directory, $"vustudio-{today:yyyyMMdd}.log");
                    writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8) { AutoFlush = false };
                    Prune();
                }

                await writer.WriteLineAsync(line).ConfigureAwait(false);
                if (_queue.Reader.Count == 0)
                {
                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
        }
        catch (IOException)
        {
            // Disk full or file locked: logging must never take the service down.
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
            foreach (var old in Directory.GetFiles(_directory, "vustudio-*.log").Order().SkipLast(RetainedFiles))
            {
                File.Delete(old);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        private readonly string _shortCategory = category[(category.LastIndexOf('.') + 1)..];

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
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {_shortCategory}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            owner.Enqueue(line);
        }
    }
}
