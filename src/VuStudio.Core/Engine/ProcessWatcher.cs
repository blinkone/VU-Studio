using System.Diagnostics;

namespace VuStudio.Core.Engine;

/// <summary>
/// Keeps a cached set of running executable names, refreshed at most every <see cref="RefreshInterval"/>
/// on a background task so the engine loop never blocks on <see cref="Process.GetProcesses()"/>.
/// </summary>
public sealed class ProcessWatcher
{
    private readonly TimeProvider _time;
    private volatile IReadOnlySet<string> _names = new HashSet<string>(StringComparer.Ordinal);
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private int _refreshing;

    public ProcessWatcher(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Lower-case names with ".exe" appended on Windows-style names lacking an extension.</summary>
    public IReadOnlySet<string> Names
    {
        get
        {
            RequestRefresh();
            return _names;
        }
    }

    private void RequestRefresh()
    {
        var now = _time.GetUtcNow();
        if (now - _lastRefresh < RefreshInterval || Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        _lastRefresh = now;
        _ = Task.Run(() =>
        {
            try
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var process in Process.GetProcesses())
                {
                    using (process)
                    {
                        try
                        {
                            var name = process.ProcessName.ToLowerInvariant();
                            set.Add(name.EndsWith(".exe", StringComparison.Ordinal) ? name : name + ".exe");
                        }
                        catch (InvalidOperationException)
                        {
                            // exited meanwhile
                        }
                    }
                }

                _names = set;
            }
            catch
            {
                // keep the previous snapshot
            }
            finally
            {
                Volatile.Write(ref _refreshing, 0);
            }
        });
    }
}
