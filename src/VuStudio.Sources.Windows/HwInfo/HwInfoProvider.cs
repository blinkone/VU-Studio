using System.Text;
using Microsoft.Extensions.Logging;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Windows.Common;

namespace VuStudio.Sources.Windows.HwInfo;

/// <summary>Parsed settings of the <c>hwinfo</c> provider.</summary>
internal sealed record HwInfoSettings
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);

    public IReadOnlyList<string> Include { get; init; } = [];

    public bool SkipDuplicates { get; init; } = true;

    public static HwInfoSettings From(ProviderSettings s) => new()
    {
        Interval = TimeSpan.FromSeconds(Math.Clamp(s.GetDouble("interval", 1), 0.25, 60)),
        Include = s.GetStringList("include"),
        SkipDuplicates = s.GetBool("skipDuplicates", true),
    };

    public bool Matches(string sensorName, string label) =>
        Include.Count == 0 ||
        Include.Any(f => sensorName.Contains(f, StringComparison.OrdinalIgnoreCase) || label.Contains(f, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Local sensor ids of the HWiNFO provider.</summary>
internal static class HwInfoIds
{
    /// <summary><c>{sensorId:x8}-{instance}/{readingId:x8}</c>, e.g. <c>f0000300-0/08000000</c>.</summary>
    public static string Format(uint sensorId, uint instance, uint readingId) => $"{sensorId:x8}-{instance}/{readingId:x8}";
}

/// <summary>
/// HWiNFO shared-memory provider. Platform neutral: the memory is reached through an opener, so the whole
/// logic runs against synthetic buffers in tests. Polls at the configured interval while HWiNFO is available and
/// retries every <paramref name="retryDelay"/> (10 s) while it is not.
/// </summary>
internal sealed class HwInfoProvider : ISensorProvider
{
    private readonly HwInfoSettings _settings;
    private readonly HwInfoOpener _opener;
    private readonly HwInfoReader _reader;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan _retryDelay;
    private readonly Dictionary<Key, State> _states = [];
    private readonly Dictionary<(uint, uint, uint), int> _occurrences = [];

    private long _pass;
    private long _lastPollTime = -1;
    private bool _unavailable;
    private int _unstable;
    private (ProviderHealth, string?)? _lastReported;

    public HwInfoProvider(string id, HwInfoSettings settings, HwInfoOpener opener, Encoding ansi, ILogger log, TimeProvider? time = null, TimeSpan? retryDelay = null)
    {
        Id = id;
        _settings = settings;
        _opener = opener;
        _reader = new HwInfoReader(ansi);
        _log = log;
        _time = time ?? TimeProvider.System;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(10);
    }

    public string Id { get; }

    public async Task RunAsync(ISensorSink sink, IProviderStatus status, CancellationToken cancellationToken)
    {
        status.Report(ProviderHealth.Starting);
        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = PollOnce(sink, status) ? _settings.Interval : _retryDelay;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "HWiNFO poll failed");
                Report(status, ProviderHealth.Degraded, ex.Message);
                delay = _retryDelay;
            }

            try
            {
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>One poll. Returns true when data was read and the normal interval applies.</summary>
    internal bool PollOnce(ISensorSink sink, IProviderStatus status)
    {
        var open = _opener();
        if (open.Memory is null)
        {
            GoUnavailable(sink, status, ProviderHealth.Unavailable, open.Failure ?? HwInfoMessages.NotRunning);
            return false;
        }

        HwInfoReadResult result;
        using (open.Memory)
        {
            result = _reader.Read(open.Memory);
        }

        switch (result.Status)
        {
            case HwInfoReadStatus.Dead:
                GoUnavailable(sink, status, ProviderHealth.Unavailable, HwInfoMessages.ShuttingDown);
                return false;
            case HwInfoReadStatus.BadSignature:
            case HwInfoReadStatus.BadLayout:
                GoUnavailable(sink, status, ProviderHealth.Unavailable, HwInfoMessages.BadData);
                return false;
            case HwInfoReadStatus.Inconsistent:
                // Keep the previous values; only complain when it persists.
                if (++_unstable >= 3)
                {
                    Report(status, ProviderHealth.Degraded, HwInfoMessages.Unstable.En);
                }

                return true;
        }

        _unstable = 0;
        var snapshot = result.Snapshot!;
        var header = snapshot.Header;
        if (snapshot.Readings.Length == 0)
        {
            GoUnavailable(sink, status, ProviderHealth.Unavailable, HwInfoMessages.Empty);
            return false;
        }

        var age = _time.GetUtcNow().ToUnixTimeSeconds() - header.PollTime;
        var staleAfter = Math.Max(60, 3 * header.PollingPeriodMs / 1000.0);
        if (age > staleAfter)
        {
            GoUnavailable(sink, status, ProviderHealth.Unavailable, HwInfoMessages.Stale);
            return false;
        }

        Publish(sink, snapshot);
        _unavailable = false;
        Report(status, ProviderHealth.Ok, null);
        return true;
    }

    private void GoUnavailable(ISensorSink sink, IProviderStatus status, ProviderHealth health, LocalizedText message)
    {
        if (!_unavailable)
        {
            _unavailable = true;
            foreach (var state in _states.Values)
            {
                if (state.HasValue)
                {
                    sink.Invalidate(state.LocalId);
                    state.HasValue = false;
                }
            }

            _lastPollTime = -1;
        }

        Report(status, health, message.En);
    }

    private void Report(IProviderStatus status, ProviderHealth health, string? message)
    {
        if (_lastReported is { } last && last.Item1 == health && last.Item2 == message)
        {
            return;
        }

        _lastReported = (health, message);
        status.Report(health, message);
    }

    private void Publish(ISensorSink sink, HwInfoSnapshot snapshot)
    {
        _pass++;
        _occurrences.Clear();
        var header = snapshot.Header;
        var pollChanged = header.PollTime != _lastPollTime;
        _lastPollTime = header.PollTime;
        var staleAfter = Math.Max(10, 4 * Math.Max(_settings.Interval.TotalSeconds, header.PollingPeriodMs / 1000.0));

        foreach (var reading in snapshot.Readings)
        {
            if (reading.SensorIndex >= snapshot.Sensors.Length)
            {
                continue;
            }

            var sensor = snapshot.Sensors[reading.SensorIndex];
            if (!_settings.Matches(sensor.Name, reading.Label))
            {
                continue;
            }

            var triple = (sensor.Id, sensor.Instance, reading.ReadingId);
            _occurrences.TryGetValue(triple, out var occurrence);
            _occurrences[triple] = occurrence + 1;
            if (occurrence > 0 && _settings.SkipDuplicates)
            {
                continue;
            }

            var key = new Key(sensor.Id, sensor.Instance, reading.ReadingId, occurrence);
            var unit = HwInfoUnits.Canonicalize(reading.Unit);
            if (!_states.TryGetValue(key, out var state))
            {
                var id = HwInfoIds.Format(sensor.Id, sensor.Instance, reading.ReadingId);
                state = new State(occurrence == 0 ? id : $"{id}~{occurrence + 1}");
                _states[key] = state;
            }

            if (state.Name != reading.Label || state.Device != sensor.Name || state.RawUnit != reading.Unit || state.Type != reading.Type)
            {
                state.Name = reading.Label;
                state.Device = sensor.Name;
                state.RawUnit = reading.Unit;
                state.Type = reading.Type;
                var category = HwInfoCategories.FromSensorName(sensor.Name);
                var kind = HwInfoUnits.Kind(reading.Type, unit);
                var (min, max) = SensorPresentation.Range(kind, category);
                sink.Define(new SensorInfo
                {
                    Id = state.LocalId,
                    Name = reading.Label,
                    Device = sensor.Name,
                    Category = category,
                    Kind = kind,
                    Unit = unit.Unit,
                    Min = min,
                    Max = max,
                    Icon = SensorPresentation.Icon(kind, category),
                    StaleAfterSeconds = staleAfter,
                });
            }

            state.LastSeenPass = _pass;
            var value = unit.Convert(reading.Value);
            if (!double.IsFinite(value))
            {
                if (state.HasValue)
                {
                    sink.Invalidate(state.LocalId);
                    state.HasValue = false;
                }

                continue;
            }

            if (pollChanged || !state.HasValue || value != state.LastValue)
            {
                sink.Publish(state.LocalId, value);
                state.LastValue = value;
                state.HasValue = true;
            }
        }

        List<Key>? gone = null;
        foreach (var (key, state) in _states)
        {
            if (state.LastSeenPass != _pass)
            {
                (gone ??= []).Add(key);
            }
        }

        if (gone is not null)
        {
            foreach (var key in gone)
            {
                sink.Remove(_states[key].LocalId);
                _states.Remove(key);
            }
        }
    }

    private readonly record struct Key(uint SensorId, uint Instance, uint ReadingId, int Duplicate);

    private sealed class State(string localId)
    {
        public string LocalId { get; } = localId;

        public string Name { get; set; } = "";

        public string Device { get; set; } = "";

        public string RawUnit { get; set; } = "";

        public HwInfoReadingType Type { get; set; } = (HwInfoReadingType)(-1);

        public bool HasValue { get; set; }

        public double LastValue { get; set; }

        public long LastSeenPass { get; set; }
    }
}
