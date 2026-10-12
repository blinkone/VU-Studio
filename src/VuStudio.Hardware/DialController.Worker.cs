using Microsoft.Extensions.Logging;
using VuStudio.Core.Engine;
using VuStudio.Core.Lighting;
using VuStudio.Core.Profiles;
using VuStudio.Hardware.Protocol;

namespace VuStudio.Hardware;

public sealed partial class DialController
{
    /// <summary>Consecutive failures after which a dial is skipped until the next rescan (<c>docs/vu1-protocol.md</c> §2).</summary>
    private const int MaxConsecutiveFailures = 5;

    private const int IdentifyHigh = 75;
    private const int IdentifyLow = 25;

    /// <summary>Longest idle wait; the worker re-checks its state at least this often.</summary>
    private static readonly TimeSpan MaxIdleWait = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan IdentifyPhase = TimeSpan.FromMilliseconds(250);
    private static readonly LedLevels IdentifyFlash = new(100, 100, 100, 100);

    /// <summary>Fast easing during identify, so the wiggle and the flashes are crisp.</summary>
    private static readonly Easing IdentifyEasing = new(25, 20);

    private long Now => _time.GetTimestamp();

    /// <summary>The worker: sends desired-state differences until stopped, asked to reconnect, or the link fails.</summary>
    private async Task ServeAsync(Connection connection, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (connection.Lost is { } lost)
            {
                throw new IOException($"Lost the connection to {connection.PortName}: {lost.Message}", lost);
            }

            if (_stopRequested)
            {
                await ParkAsync(connection, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (_reconnectRequested)
            {
                return;
            }

            var options = Options;
            if (!ReferenceEquals(connection.Client.Timeouts, options.Timeouts))
            {
                connection.Client.Timeouts = options.Timeouts;
            }

            SyncTargets(connection);
            var now = Now;
            connection.NextWake = now + Ticks(MaxIdleWait);
            connection.Progressed = false;
            UpdateIdentify(connection, now);

            await SendNeedlesAsync(connection, options, cancellationToken).ConfigureAwait(false);
            await SendBacklightsAsync(connection, cancellationToken).ConfigureAwait(false);
            await RunWorkAsync(connection, cancellationToken).ConfigureAwait(false);
            await SendEasingAsync(connection, cancellationToken).ConfigureAwait(false);
            await SendFaceStepAsync(connection, options, cancellationToken).ConfigureAwait(false);
            await RecoverAsync(connection, options, cancellationToken).ConfigureAwait(false);

            if (!connection.Progressed)
            {
                var wait = connection.NextWake - Now;
                if (wait > 0)
                {
                    await _signal.WaitAsync(ToTimeSpan(wait), cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>Copies the desired state into the links and applies <see cref="Invalidate"/>.</summary>
    private void SyncTargets(Connection connection)
    {
        var invalidation = Volatile.Read(ref _invalidation);
        if (invalidation != connection.Invalidation)
        {
            connection.Invalidation = invalidation;
            foreach (var link in connection.Links.Values)
            {
                link.ForgetSent();
            }

            _logger.LogDebug("Sending every desired value again");
        }

        lock (_lock)
        {
            foreach (var link in connection.Links.Values)
            {
                var target = _targets.GetValueOrDefault(link.Uid);
                link.DesiredNeedle = target?.Needle;
                link.DesiredBacklight = target?.Backlight;
                link.DesiredNeedleEasing = target?.NeedleEasing;
                link.DesiredBacklightEasing = target?.BacklightEasing;
                link.DesiredFace = target?.Face;
            }
        }
    }

    private async Task SendNeedlesAsync(Connection connection, DialControllerOptions options, CancellationToken cancellationToken)
    {
        foreach (var link in connection.Links.Values)
        {
            if (link.Unresponsive || link.EffectiveNeedle is not { } value || value == link.SentNeedle)
            {
                continue;
            }

            var now = Now;
            if (!IsDue(connection, link.RetryAt, now) ||
                (link.NeedleSentAt is { } sentAt && !IsDue(connection, sentAt + Ticks(options.NeedleInterval), now)))
            {
                continue;
            }

            if (await TrySendAsync(connection, link, (client, ct) => client.SetPercentAsync(link.BusIndex, value, ct), cancellationToken).ConfigureAwait(false))
            {
                link.SentNeedle = value;
                link.NeedleSentAt = Now;
            }
        }
    }

    private async Task SendBacklightsAsync(Connection connection, CancellationToken cancellationToken)
    {
        foreach (var link in connection.Links.Values)
        {
            if (link.Unresponsive || link.EffectiveBacklight is not { } levels || levels == link.SentBacklight || !IsDue(connection, link.RetryAt, Now))
            {
                continue;
            }

            if (await TrySendAsync(connection, link, (client, ct) => client.SetBacklightAsync(link.BusIndex, levels, ct), cancellationToken).ConfigureAwait(false))
            {
                link.SentBacklight = levels;
            }
        }
    }

    private async Task SendEasingAsync(Connection connection, CancellationToken cancellationToken)
    {
        foreach (var link in connection.Links.Values)
        {
            if (link.Unresponsive || !IsDue(connection, link.RetryAt, Now))
            {
                continue;
            }

            if (link.EffectiveNeedleEasing is { } needle &&
                !await SendEasingAsync(connection, link, needle, backlight: false, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            if (link.EffectiveBacklightEasing is { } backlight)
            {
                await SendEasingAsync(connection, link, backlight, backlight: true, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Sends the step and the period that differ from what the dial acknowledged.</summary>
    private async Task<bool> SendEasingAsync(Connection connection, DialLink link, Easing easing, bool backlight, CancellationToken cancellationToken)
    {
        var (sentStep, sentPeriod) = backlight ? (link.SentBacklightStep, link.SentBacklightPeriod) : (link.SentNeedleStep, link.SentNeedlePeriod);
        if (sentStep == easing.Step && sentPeriod == easing.PeriodMs)
        {
            return true;
        }

        var index = link.BusIndex;
        if (sentStep != easing.Step)
        {
            var sent = await TrySendAsync(
                connection,
                link,
                backlight
                    ? (client, ct) => client.SetBacklightEasingStepAsync(index, easing.Step, ct)
                    : (client, ct) => client.SetNeedleEasingStepAsync(index, easing.Step, ct),
                cancellationToken).ConfigureAwait(false);
            if (!sent)
            {
                return false;
            }

            if (backlight)
            {
                link.SentBacklightStep = easing.Step;
            }
            else
            {
                link.SentNeedleStep = easing.Step;
            }
        }

        if (sentPeriod != easing.PeriodMs)
        {
            var sent = await TrySendAsync(
                connection,
                link,
                backlight
                    ? (client, ct) => client.SetBacklightEasingPeriodAsync(index, easing.PeriodMs, ct)
                    : (client, ct) => client.SetNeedleEasingPeriodAsync(index, easing.PeriodMs, ct),
                cancellationToken).ConfigureAwait(false);
            if (!sent)
            {
                return false;
            }
        }

        link.AcceptEasing(easing, backlight);
        PublishDials(connection);
        return true;
    }

    /// <summary>
    /// One step of the face upload state machine: clear (white) → cursor 0,0 → image frames of at most 1000 bytes
    /// with <see cref="DialControllerOptions.InterChunkDelay"/> after each → show. A failed step restarts the upload
    /// from the beginning once the dial's backoff has passed. One upload runs at a time, dials take turns.
    /// </summary>
    private async Task SendFaceStepAsync(Connection connection, DialControllerOptions options, CancellationToken cancellationToken)
    {
        var upload = connection.Upload ??= StartNextUpload(connection);
        if (upload is null)
        {
            return;
        }

        var link = upload.Link;
        if (link.Unresponsive || !connection.Links.ContainsKey(link.Uid))
        {
            connection.Upload = null;
            return;
        }

        var now = Now;
        if (!IsDue(connection, link.RetryAt, now) || !IsDue(connection, upload.NotBefore, now))
        {
            return;
        }

        var index = link.BusIndex;
        var chunk = upload.Stage == FaceStage.Data ? upload.NextChunk() : ReadOnlyMemory<byte>.Empty;
        Func<VuHubClient, CancellationToken, Task> send = upload.Stage switch
        {
            FaceStage.Clear => (client, ct) => client.DisplayClearAsync(index, black: false, ct),
            FaceStage.Cursor => (client, ct) => client.DisplayGotoXYAsync(index, 0, 0, ct),
            FaceStage.Data => (client, ct) => client.DisplayImageDataAsync(index, chunk, ct),
            _ => (client, ct) => client.DisplayShowAsync(index, ct),
        };

        if (!await TrySendAsync(connection, link, send, cancellationToken).ConfigureAwait(false))
        {
            connection.Upload = null;
            return;
        }

        switch (upload.Stage)
        {
            case FaceStage.Clear:
                upload.Stage = FaceStage.Cursor;
                break;
            case FaceStage.Cursor:
                upload.Stage = FaceStage.Data;
                break;
            case FaceStage.Data:
                upload.Offset += chunk.Length;
                upload.NotBefore = Now + Ticks(options.InterChunkDelay);
                if (upload.Offset >= upload.Image.Data.Length)
                {
                    upload.Stage = FaceStage.Show;
                }

                break;
            default:
                link.UploadedFace = upload.Image.Hash;
                connection.Upload = null;
                _logger.LogDebug("Uploaded a face to dial {Uid} in {Elapsed:0} ms", link.Uid, ToTimeSpan(Now - upload.StartedAt).TotalMilliseconds);
                break;
        }
    }

    private FaceUpload? StartNextUpload(Connection connection)
    {
        var now = Now;
        var pending = connection.Links.Values
            .Where(l => !l.Unresponsive && l.DesiredFace is { } face && face.Hash != l.UploadedFace && IsDue(connection, l.RetryAt, now))
            .OrderBy(l => l.BusIndex)
            .ToList();
        if (pending.Count == 0)
        {
            return null;
        }

        var link = pending.FirstOrDefault(l => l.BusIndex > connection.LastFaceIndex) ?? pending[0];
        connection.LastFaceIndex = link.BusIndex;
        return new FaceUpload(link, link.DesiredFace!, now);
    }

    private async Task RunWorkAsync(Connection connection, CancellationToken cancellationToken)
    {
        while (_work.TryDequeue(out var item))
        {
            if (item.IsCancelled)
            {
                item.Abandon(null);
                continue;
            }

            connection.Progressed = true;
            try
            {
                await item.RunAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                item.Abandon(ex);
                throw;
            }
        }
    }

    private Task StartIdentify(Connection connection, string uid, WorkItem item)
    {
        if (!connection.Links.TryGetValue(uid, out var link) || link.Unresponsive)
        {
            item.Complete(false);
            return Task.CompletedTask;
        }

        if (link.Identify is { } running)
        {
            running.Request.Complete(false);
        }
        else
        {
            // Restore what the dial showed when nothing else is desired afterwards.
            link.FallbackNeedle = link.SentNeedle ?? 0;
            link.FallbackBacklight = link.SentBacklight ?? LedLevels.Off;
            link.FallbackNeedleEasing = link.KnownNeedleEasing ?? Easing.DefaultNeedle;
            link.FallbackBacklightEasing = link.KnownBacklightEasing ?? Easing.DefaultBacklight;
        }

        var now = Now;
        link.Identify = new IdentifyState(item, now, now + Ticks(Options.IdentifyDuration));
        _logger.LogInformation("Identifying dial {Uid}", uid);
        return Task.CompletedTask;
    }

    /// <summary>Advances identify animations, ends finished or cancelled ones, and schedules the next phase.</summary>
    private void UpdateIdentify(Connection connection, long now)
    {
        foreach (var link in connection.Links.Values)
        {
            if (link.Identify is not { } identify)
            {
                continue;
            }

            if (now >= identify.End || identify.Request.IsCancelled)
            {
                EndIdentify(link);
                continue;
            }

            var phaseTicks = Math.Max(1, Ticks(IdentifyPhase));
            identify.Phase = (int)((now - identify.Start) / phaseTicks);
            IsDue(connection, Math.Min(identify.Start + ((identify.Phase + 1) * phaseTicks), identify.End), now);
        }
    }

    private static void EndIdentify(DialLink link)
    {
        if (link.Identify is { } identify)
        {
            link.Identify = null;
            identify.Request.Complete(true);
        }
    }

    private async Task RawWorkAsync(Connection connection, string uid, int raw, WorkItem item, CancellationToken cancellationToken)
    {
        if (!connection.Links.TryGetValue(uid, out var link) || link.Unresponsive)
        {
            item.Complete(false);
            return;
        }

        item.Complete(await TrySendAsync(connection, link, (client, ct) => client.SetRawAsync(link.BusIndex, raw, ct), cancellationToken).ConfigureAwait(false));
    }

    private async Task CalibrateWorkAsync(Connection connection, string uid, CalibrationPoint point, int raw, WorkItem item, CancellationToken cancellationToken)
    {
        if (!connection.Links.TryGetValue(uid, out var link) || link.Unresponsive)
        {
            item.Complete(false);
            return;
        }

        var ok = await TrySendAsync(
            connection,
            link,
            point == CalibrationPoint.Full
                ? (client, ct) => client.CalibrateMaxAsync(link.BusIndex, raw, ct)
                : (client, ct) => client.CalibrateHalfAsync(link.BusIndex, raw, ct),
            cancellationToken).ConfigureAwait(false);
        if (ok)
        {
            _logger.LogInformation("Calibrated the {Point} point of dial {Uid} to raw {Raw}", point, uid, raw);

            // The same percent now maps to another raw value: send it again.
            link.SentNeedle = null;
        }

        item.Complete(ok);
    }

    private async Task RescanWorkAsync(Connection connection, bool provision, WorkItem item, CancellationToken cancellationToken)
    {
        _logger.LogInformation(provision ? "Provisioning and rescanning the bus" : "Rescanning the bus");
        await EnumerateAsync(connection, provisionIfEmpty: false, forceProvision: provision, cancellationToken).ConfigureAwait(false);
        item.Complete(true);
    }

    /// <summary>Rescans periodically while a dial is unresponsive or none was found.</summary>
    private async Task RecoverAsync(Connection connection, DialControllerOptions options, CancellationToken cancellationToken)
    {
        if (options.RecoveryInterval <= TimeSpan.Zero ||
            (connection.Links.Count > 0 && !connection.Links.Values.Any(l => l.Unresponsive)) ||
            !IsDue(connection, connection.LastScan + Ticks(options.RecoveryInterval), Now))
        {
            return;
        }

        if (connection.Links.Count == 0)
        {
            _logger.LogDebug("No dials found; rescanning the bus");
        }
        else
        {
            _logger.LogInformation("Rescanning the bus to recover unresponsive dials");
        }

        connection.Progressed = true;
        await EnumerateAsync(connection, provisionIfEmpty: false, forceProvision: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task ParkAsync(Connection connection, CancellationToken cancellationToken)
    {
        if (!Options.ParkOnStop)
        {
            return;
        }

        var parked = 0;
        foreach (var link in connection.Links.Values.Where(l => !l.Unresponsive))
        {
            try
            {
                await connection.Client.SetPercentAsync(link.BusIndex, 0, cancellationToken).ConfigureAwait(false);
                await connection.Client.SetBacklightAsync(link.BusIndex, LedLevels.Off, cancellationToken).ConfigureAwait(false);
                parked++;
            }
            catch (VuHubException ex)
            {
                _logger.LogDebug("Parking dial {Uid} failed: {Message}", link.Uid, ex.Message);
            }
        }

        _logger.LogInformation("Parked {Count} dial(s)", parked);
    }

    /// <summary>
    /// Sends one command to a dial. A failed or timed-out command backs the dial off; after
    /// <see cref="MaxConsecutiveFailures"/> in a row it is reported offline. Link failures propagate.
    /// </summary>
    private async Task<bool> TrySendAsync(Connection connection, DialLink link, Func<VuHubClient, CancellationToken, Task> send, CancellationToken cancellationToken)
    {
        connection.Progressed = true;
        try
        {
            await send(connection.Client, cancellationToken).ConfigureAwait(false);
        }
        catch (VuHubException ex)
        {
            OnDialFailure(connection, link, ex);
            return false;
        }

        if (link.Failures > 0)
        {
            _logger.LogInformation("Dial {Uid} answers again", link.Uid);
        }

        link.Failures = 0;
        link.RetryAt = 0;
        link.Backoff = Options.RetryDelay;
        return true;
    }

    private void OnDialFailure(Connection connection, DialLink link, VuHubException error)
    {
        link.Failures++;
        if (link.Failures >= MaxConsecutiveFailures)
        {
            link.Unresponsive = true;
            _logger.LogWarning(
                "Dial {Uid} at bus index {Index} failed {Count} times in a row ({Message}); skipping it until the next rescan",
                link.Uid,
                link.BusIndex,
                link.Failures,
                error.Message);
            EndIdentify(link);
            if (connection.Upload?.Link == link)
            {
                connection.Upload = null;
            }

            PublishDials(connection);
            return;
        }

        var options = Options;
        link.RetryAt = Now + Ticks(link.Backoff);
        if (link.Failures == 1)
        {
            _logger.LogWarning("Dial {Uid}: {Message}; retrying in {Delay}", link.Uid, error.Message, link.Backoff);
        }
        else
        {
            _logger.LogDebug("Dial {Uid}: {Message} ({Count} in a row); retrying in {Delay}", link.Uid, error.Message, link.Failures, link.Backoff);
        }

        link.Backoff = TimeSpan.FromTicks(Math.Min(link.Backoff.Ticks * 2, Math.Max(options.MaxRetryDelay.Ticks, options.RetryDelay.Ticks)));
    }

    /// <summary>True when <paramref name="due"/> has passed; otherwise records it as a wake-up time.</summary>
    private static bool IsDue(Connection connection, long due, long now)
    {
        if (now >= due)
        {
            return true;
        }

        if (due < connection.NextWake)
        {
            connection.NextWake = due;
        }

        return false;
    }

    private long Ticks(TimeSpan span) => span <= TimeSpan.Zero ? 0 : (long)(span.TotalSeconds * _time.TimestampFrequency);

    private TimeSpan ToTimeSpan(long ticks) => TimeSpan.FromSeconds((double)ticks / _time.TimestampFrequency);

    private enum FaceStage
    {
        Clear,
        Cursor,
        Data,
        Show,
    }

    /// <summary>A dial on the current connection: desired, acknowledged and health state. Touched only by the worker.</summary>
    private sealed class DialLink(string uid, int busIndex, TimeSpan retryDelay)
    {
        public string Uid { get; } = uid;

        public int BusIndex { get; set; } = busIndex;

        public string? FirmwareVersion { get; set; }

        public string? HardwareVersion { get; set; }

        public string? ProtocolVersion { get; set; }

        public string? BuildHash { get; set; }

        /// <summary>The easing the dial is known to have (read at enumeration or acknowledged since).</summary>
        public Easing? KnownNeedleEasing { get; private set; }

        public Easing? KnownBacklightEasing { get; private set; }

        public int? DesiredNeedle { get; set; }

        public LedLevels? DesiredBacklight { get; set; }

        public Easing? DesiredNeedleEasing { get; set; }

        public Easing? DesiredBacklightEasing { get; set; }

        public FaceImage? DesiredFace { get; set; }

        /// <summary>Restored after an identify when nothing is desired.</summary>
        public int? FallbackNeedle { get; set; }

        public LedLevels? FallbackBacklight { get; set; }

        public Easing? FallbackNeedleEasing { get; set; }

        public Easing? FallbackBacklightEasing { get; set; }

        public int? SentNeedle { get; set; }

        public long? NeedleSentAt { get; set; }

        public LedLevels? SentBacklight { get; set; }

        public int? SentNeedleStep { get; set; }

        public int? SentNeedlePeriod { get; set; }

        public int? SentBacklightStep { get; set; }

        public int? SentBacklightPeriod { get; set; }

        public ulong? UploadedFace { get; set; }

        public int Failures { get; set; }

        /// <summary>Timestamp before which the dial is not retried after a failure.</summary>
        public long RetryAt { get; set; }

        public TimeSpan Backoff { get; set; } = retryDelay;

        public bool Unresponsive { get; set; }

        public IdentifyState? Identify { get; set; }

        public int? EffectiveNeedle => Identify is { } identify
            ? (identify.Phase % 2 == 0 ? IdentifyHigh : IdentifyLow)
            : DesiredNeedle ?? FallbackNeedle;

        public LedLevels? EffectiveBacklight => Identify is { } identify
            ? (identify.Phase % 2 == 0 ? IdentifyFlash : LedLevels.Off)
            : DesiredBacklight ?? FallbackBacklight;

        public Easing? EffectiveNeedleEasing => Identify is not null ? IdentifyEasing : DesiredNeedleEasing ?? FallbackNeedleEasing;

        public Easing? EffectiveBacklightEasing => Identify is not null ? IdentifyEasing : DesiredBacklightEasing ?? FallbackBacklightEasing;

        /// <summary>Records easing the dial has (acknowledged or read back).</summary>
        public void AcceptEasing(Easing easing, bool backlight)
        {
            if (backlight)
            {
                SentBacklightStep = easing.Step;
                SentBacklightPeriod = easing.PeriodMs;
                KnownBacklightEasing = easing;
            }
            else
            {
                SentNeedleStep = easing.Step;
                SentNeedlePeriod = easing.PeriodMs;
                KnownNeedleEasing = easing;
            }
        }

        /// <summary>Makes every desired value count as unsent.</summary>
        public void ForgetSent()
        {
            SentNeedle = null;
            NeedleSentAt = null;
            SentBacklight = null;
            SentNeedleStep = null;
            SentNeedlePeriod = null;
            SentBacklightStep = null;
            SentBacklightPeriod = null;
            UploadedFace = null;
        }

        public void Revive(TimeSpan retryDelay)
        {
            Unresponsive = false;
            Failures = 0;
            RetryAt = 0;
            Backoff = retryDelay;
            ForgetSent();
        }

        public DialInfo ToInfo() => new(
            Uid,
            BusIndex,
            Online: !Unresponsive,
            FirmwareVersion,
            HardwareVersion,
            ProtocolVersion,
            BuildHash,
            KnownNeedleEasing,
            KnownBacklightEasing);
    }

    private sealed class FaceUpload(DialLink link, FaceImage image, long startedAt)
    {
        public DialLink Link { get; } = link;

        public FaceImage Image { get; } = image;

        public long StartedAt { get; } = startedAt;

        public FaceStage Stage { get; set; } = FaceStage.Clear;

        public int Offset { get; set; }

        public long NotBefore { get; set; }

        public ReadOnlyMemory<byte> NextChunk() =>
            Image.Data.AsMemory(Offset, Math.Min(VuProtocol.MaxImageChunk, Image.Data.Length - Offset));
    }

    private sealed class IdentifyState(WorkItem request, long start, long end)
    {
        public WorkItem Request { get; } = request;

        public long Start { get; } = start;

        public long End { get; } = end;

        public int Phase { get; set; }
    }

    /// <summary>An operation the worker runs on behalf of a caller, who awaits <see cref="Completion"/>.</summary>
    private sealed class WorkItem(Func<Connection, WorkItem, CancellationToken, Task> run, CancellationToken callerToken, bool throwOnFailure = false)
    {
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> Completion => _completion.Task;

        public bool IsCancelled => callerToken.IsCancellationRequested;

        public Task RunAsync(Connection connection, CancellationToken cancellationToken) => run(connection, this, cancellationToken);

        public void Complete(bool result) => _completion.TrySetResult(result);

        /// <summary>Ends the item without running it to completion: false, or the error for callers that expect one.</summary>
        public void Abandon(Exception? error)
        {
            if (error is OperationCanceledException)
            {
                _completion.TrySetCanceled();
            }
            else if (error is not null && throwOnFailure && _completion.TrySetException(error))
            {
                // Mark the exception observed: the caller may have stopped waiting.
                _ = _completion.Task.Exception;
            }
            else
            {
                _completion.TrySetResult(false);
            }
        }
    }

    /// <summary>An auto-reset signal with a single waiter (the worker).</summary>
    private sealed class WorkSignal(TimeProvider time)
    {
        private readonly Lock _lock = new();
        private TaskCompletionSource? _waiter;
        private bool _set;

        public void Set()
        {
            TaskCompletionSource? waiter;
            lock (_lock)
            {
                waiter = _waiter;
                _waiter = null;
                _set = waiter is null;
            }

            waiter?.TrySetResult();
        }

        /// <summary>Returns when signalled or after <paramref name="timeout"/>.</summary>
        public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            TaskCompletionSource waiter;
            lock (_lock)
            {
                if (_set)
                {
                    _set = false;
                    return;
                }

                waiter = _waiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            try
            {
                await waiter.Task.WaitAsync(timeout, time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            finally
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_waiter, waiter))
                    {
                        _waiter = null;
                    }
                }
            }
        }
    }
}
