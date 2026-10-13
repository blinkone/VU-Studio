using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using VuStudio.Core.Faces;
using VuStudio.Core.Sensors;

namespace VuStudio.Core.History;

/// <summary>One minute of a sensor's values.</summary>
public sealed record HistoryAggregate(string SensorId, DateTimeOffset Minute, double Min, double Avg, double Max, int Count);

/// <summary>Long-term storage of per-minute aggregates (SQLite in the service).</summary>
public interface IHistoryPersistence
{
    void Append(IReadOnlyList<HistoryAggregate> rows);

    IReadOnlyList<HistoryAggregate> Query(string sensorId, DateTimeOffset from, DateTimeOffset to);

    void Prune(DateTimeOffset olderThan);
}

/// <summary>
/// Samples the recorded sensors once per second into in-memory ring buffers (last hour, used by
/// sparklines and live charts) and flushes per-minute min/avg/max aggregates to persistence.
/// </summary>
public sealed class HistoryRecorder
{
    private const int Capacity = 3600;

    /// <summary>Most sensors that can be added on demand with <see cref="Track"/> (on top of the configured ones).</summary>
    public const int MaxTracked = 2000;

    private readonly SensorHub _hub;
    private readonly IHistoryPersistence? _persistence;
    private readonly ILogger<HistoryRecorder> _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Series> _series = new(StringComparer.Ordinal);
    private volatile HashSet<string> _recorded = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _tracked = new(StringComparer.Ordinal);
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    public HistoryRecorder(SensorHub hub, ILogger<HistoryRecorder> logger, IHistoryPersistence? persistence = null, TimeProvider? time = null)
    {
        _hub = hub;
        _logger = logger;
        _persistence = persistence;
        _time = time ?? TimeProvider.System;
    }

    public int RetentionDays { get; set; } = 30;

    public bool PersistenceEnabled { get; set; } = true;

    /// <summary>The configured sensors plus the ones added with <see cref="Track"/>.</summary>
    public IReadOnlyCollection<string> RecordedSensors => _recorded.Union(_tracked.Keys, StringComparer.Ordinal).ToList();

    /// <summary>Number of sensors added on demand with <see cref="Track"/>.</summary>
    public int TrackedCount => _tracked.Count;

    /// <summary>
    /// Replaces the set of configured sensors to record and forgets the on-demand ones (the UI tracks again what it
    /// still shows). Buffers of dropped sensors are kept until they age out.
    /// </summary>
    public void SetRecordedSensors(IEnumerable<string> sensorIds)
    {
        _recorded = new HashSet<string>(sensorIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal);
        _tracked.Clear();
    }

    /// <summary>
    /// Adds one sensor to the recorded set (e.g. when the UI opens its chart). Only sensors that exist in the hub are
    /// accepted, and at most <see cref="MaxTracked"/> of them, so a client cannot grow the set without bound.
    /// O(1). Returns false when the sensor is unknown or the limit is reached.
    /// </summary>
    public bool Track(string sensorId)
    {
        if (string.IsNullOrWhiteSpace(sensorId))
        {
            return false;
        }

        if (_recorded.Contains(sensorId) || _tracked.ContainsKey(sensorId))
        {
            return true;
        }

        if (!_hub.TryGet(sensorId, out _) || _tracked.Count >= MaxTracked)
        {
            return false;
        }

        _tracked.TryAdd(sensorId, 0);
        return true;
    }

    /// <summary>Takes one sample of every recorded sensor. Call once per second.</summary>
    public void Sample()
    {
        var now = _time.GetUtcNow();
        var flush = new List<HistoryAggregate>();
        var recorded = _recorded;
        foreach (var id in recorded.Concat(_tracked.Keys.Where(k => !recorded.Contains(k))))
        {
            var series = _series.GetOrAdd(id, _ => new Series());
            var value = _hub.GetNumber(id);
            lock (series)
            {
                if (value is { } v)
                {
                    series.Add(now, v);
                }

                if (series.CompleteMinute(now) is { } aggregate)
                {
                    flush.Add(aggregate with { SensorId = id });
                }
            }
        }

        if (flush.Count > 0 && PersistenceEnabled && _persistence is not null)
        {
            try
            {
                _persistence.Append(flush);
                if (now - _lastPrune > TimeSpan.FromHours(1))
                {
                    _lastPrune = now;
                    _persistence.Prune(now.AddDays(-Math.Max(1, RetentionDays)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not persist history");
            }
        }
    }

    /// <summary>Recent 1-second samples within <paramref name="window"/> (at most one hour).</summary>
    public IReadOnlyList<HistoryPoint> GetRecent(string sensorId, TimeSpan window)
    {
        if (!_series.TryGetValue(sensorId, out var series))
        {
            return [];
        }

        var since = _time.GetUtcNow() - window;
        lock (series)
        {
            return series.Since(since);
        }
    }

    /// <summary>Per-minute aggregates from persistence, plus the current partial minute.</summary>
    public IReadOnlyList<HistoryAggregate> Query(string sensorId, DateTimeOffset from, DateTimeOffset to)
    {
        var rows = new List<HistoryAggregate>();
        if (_persistence is not null)
        {
            try
            {
                rows.AddRange(_persistence.Query(sensorId, from, to));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "History query failed for {Sensor}", sensorId);
            }
        }

        if (rows.Count == 0 && _series.TryGetValue(sensorId, out var series))
        {
            lock (series)
            {
                rows.AddRange(series.MinuteAggregates(sensorId, from, to));
            }
        }

        return rows;
    }

    private sealed class Series
    {
        private readonly long[] _ticks = new long[Capacity];
        private readonly double[] _values = new double[Capacity];
        private int _start;
        private int _count;
        private long _minuteTicks;
        private double _min = double.PositiveInfinity;
        private double _max = double.NegativeInfinity;
        private double _sum;
        private int _n;

        public void Add(DateTimeOffset time, double value)
        {
            var index = (_start + _count) % Capacity;
            if (_count == Capacity)
            {
                _start = (_start + 1) % Capacity;
            }
            else
            {
                _count++;
            }

            _ticks[index] = time.UtcTicks;
            _values[index] = value;

            var minute = time.UtcTicks - (time.UtcTicks % TimeSpan.TicksPerMinute);
            if (_minuteTicks == 0)
            {
                _minuteTicks = minute;
            }

            if (minute == _minuteTicks)
            {
                _min = Math.Min(_min, value);
                _max = Math.Max(_max, value);
                _sum += value;
                _n++;
            }
        }

        /// <summary>Returns the aggregate of the previous minute once the clock moved past it.</summary>
        public HistoryAggregate? CompleteMinute(DateTimeOffset now)
        {
            var minute = now.UtcTicks - (now.UtcTicks % TimeSpan.TicksPerMinute);
            if (_minuteTicks == 0 || minute == _minuteTicks)
            {
                return null;
            }

            HistoryAggregate? result = _n > 0
                ? new HistoryAggregate("", new DateTimeOffset(_minuteTicks, TimeSpan.Zero), _min, _sum / _n, _max, _n)
                : null;
            _minuteTicks = minute;
            _min = double.PositiveInfinity;
            _max = double.NegativeInfinity;
            _sum = 0;
            _n = 0;

            // Re-seed the new minute with a sample that may already belong to it.
            if (_count > 0)
            {
                var last = (_start + _count - 1) % Capacity;
                if (_ticks[last] - (_ticks[last] % TimeSpan.TicksPerMinute) == minute)
                {
                    _min = _max = _sum = _values[last];
                    _n = 1;
                }
            }

            return result;
        }

        public IReadOnlyList<HistoryPoint> Since(DateTimeOffset since)
        {
            var list = new List<HistoryPoint>();
            for (var i = 0; i < _count; i++)
            {
                var index = (_start + i) % Capacity;
                if (_ticks[index] >= since.UtcTicks)
                {
                    list.Add(new HistoryPoint(new DateTimeOffset(_ticks[index], TimeSpan.Zero), _values[index]));
                }
            }

            return list;
        }

        public IEnumerable<HistoryAggregate> MinuteAggregates(string id, DateTimeOffset from, DateTimeOffset to) =>
            Since(from)
                .Where(p => p.Time <= to)
                .GroupBy(p => p.Time.UtcTicks - (p.Time.UtcTicks % TimeSpan.TicksPerMinute))
                .Select(g => new HistoryAggregate(id, new DateTimeOffset(g.Key, TimeSpan.Zero), g.Min(p => p.Value), g.Average(p => p.Value), g.Max(p => p.Value), g.Count()));
    }
}
