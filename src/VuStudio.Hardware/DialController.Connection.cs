using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using VuStudio.Core.Engine;
using VuStudio.Hardware.Discovery;
using VuStudio.Hardware.Protocol;
using VuStudio.Hardware.Transport;

namespace VuStudio.Hardware;

public sealed partial class DialController
{
    private const int ProvisionAttempts = 3;

    private static readonly TimeSpan ProvisionInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>While no hub is plugged in, discovery (cheap, touches no device) runs at least this often.</summary>
    private static readonly TimeSpan MaxDiscoveryDelay = TimeSpan.FromSeconds(2);

    /// <summary>Every dial seen since start, by UID; touched only by the run loop.</summary>
    private readonly Dictionary<string, DialInfo> _known = new(StringComparer.OrdinalIgnoreCase);

    private string? _attemptedPort;
    private string? _lastGoodPort;

    /// <summary>Connects, serves and reconnects with backoff until stopped. Never throws.</summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Dial controller started");
        var delay = Options.ReconnectDelay;
        var failures = 0;
        try
        {
            while (!_stopRequested)
            {
                _reconnectRequested = false;
                var options = Options;
                Connection? connection = null;
                var notFound = false;
                try
                {
                    connection = await ConnectAsync(options, cancellationToken).ConfigureAwait(false);
                    failures = 0;
                    delay = options.ReconnectDelay;
                    await ServeAsync(connection, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    failures++;
                    notFound = ex is HubNotFoundException;
                    ReportFailure(ex, connection, failures);
                }
                finally
                {
                    if (connection is not null)
                    {
                        await CloseAsync(connection).ConfigureAwait(false);
                    }
                }

                if (_stopRequested)
                {
                    break;
                }

                if (_reconnectRequested)
                {
                    continue;
                }

                await WaitForRetryAsync(notFound && delay > MaxDiscoveryDelay ? MaxDiscoveryDelay : delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, Math.Max(options.MaxReconnectDelay.Ticks, options.ReconnectDelay.Ticks)));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "The dial controller loop failed");
        }
        finally
        {
            AbandonWork();
            SetStatus(HubStatus.Disconnected);
            _logger.LogInformation("Dial controller stopped");
        }
    }

    private async Task WaitForRetryAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var start = _time.GetTimestamp();
        while (!_stopRequested && !_reconnectRequested)
        {
            var remaining = delay - _time.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            await _signal.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Opens the first candidate that answers and enumerates its dials.</summary>
    private async Task<Connection> ConnectAsync(DialControllerOptions options, CancellationToken cancellationToken)
    {
        var candidates = GetCandidates(options);
        if (candidates.Count == 0)
        {
            throw new HubNotFoundException();
        }

        SetStatus(new HubStatus(HubConnectionState.Connecting, candidates.Count == 1 ? candidates[0].PortName : null, null, options.Simulate > 0 && _transportFactory is null));
        Exception? lastError = null;
        foreach (var candidate in candidates)
        {
            var transport = candidate.Create();
            var portName = candidate.PortName ?? transport.Name;
            _attemptedPort = portName;
            Connection? connection = null;
            try
            {
                await transport.OpenAsync(cancellationToken).ConfigureAwait(false);
                var client = new VuHubClient(transport, options.Timeouts, _logger, _time);
                var opened = new Connection(transport, client, portName, transport is SimulatedVuTransport, Volatile.Read(ref _invalidation));
                connection = opened;
                transport.Disconnected += error =>
                {
                    opened.MarkLost(error);
                    _signal.Set();
                };

                if (candidate.Probe)
                {
                    await ProbeAsync(connection, options, cancellationToken).ConfigureAwait(false);
                }

                await EnumerateAsync(connection, provisionIfEmpty: true, forceProvision: false, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await DisposeQuietlyAsync(connection, transport).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                lastError = ex;
                if (candidates.Count > 1)
                {
                    _logger.LogDebug(ex, "{Port} is not a responding VU1 hub", portName);
                }

                continue;
            }

            if (candidate.PortName is not null)
            {
                _lastGoodPort = candidate.PortName;
            }

            lock (_lock)
            {
                _connection = connection;
            }

            var dials = connection.Links.Values.OrderBy(l => l.BusIndex).ToList();
            SetStatus(new HubStatus(HubConnectionState.Connected, portName, $"{dials.Count} dial(s)", connection.Simulated));
            _logger.LogInformation(
                "Connected to the VU1 hub on {Port} with {Count} dial(s): {Dials}",
                portName,
                dials.Count,
                string.Join(", ", dials.Select(l => $"#{l.BusIndex} {l.Uid} (firmware {l.FirmwareVersion ?? "?"})")));
            return connection;
        }

        ExceptionDispatchInfo.Throw(lastError!);
        throw new UnreachableException();
    }

    /// <summary>Transports to try, best first. Discovered ports are probed; an explicit port is not.</summary>
    private List<TransportCandidate> GetCandidates(DialControllerOptions options)
    {
        if (_transportFactory is not null)
        {
            return [new TransportCandidate(null, _transportFactory, Probe: false)];
        }

        if (options.Simulate > 0)
        {
            var count = Math.Min(options.Simulate, SimulatedHub.MaxDials);
            var hub = Volatile.Read(ref _simulator);
            if (hub is null || hub.DialCount != count)
            {
                hub = new SimulatedHub(count);
                Volatile.Write(ref _simulator, hub);
            }

            return [new TransportCandidate(null, () => new SimulatedVuTransport(hub, _time), Probe: false)];
        }

        Volatile.Write(ref _simulator, null);
        var probe = HubPortFinder.IsAuto(options.Port);
        return [.. HubPortFinder.Resolve(options.Port, _portLocator)
            .OrderBy(c => string.Equals(c.PortName, _lastGoodPort, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(c => new TransportCandidate(c.PortName, () => PortTransportFactory(c.PortName), probe))];
    }

    /// <summary>
    /// Every FT231X board shares the hub's VID/PID, so a discovered port must answer GET_DEVICES_MAP quickly.
    /// Any matching reply, even an error status, proves a VU1 hub is listening.
    /// </summary>
    private static async Task ProbeAsync(Connection connection, DialControllerOptions options, CancellationToken cancellationToken)
    {
        try
        {
            await connection.Client.SendAsync(VuCommand.GetDevicesMap, VuDataType.None, ReadOnlyMemory<byte>.Empty, options.ProbeTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (VuStatusException)
        {
        }
    }

    /// <summary>
    /// Rescans the bus and maps UIDs to bus indices (<c>docs/vu1-protocol.md</c> §6). Known dials keep their state;
    /// new ones have their versions and easing read; unresponsive dials that answer again are revived.
    /// </summary>
    private async Task EnumerateAsync(Connection connection, bool provisionIfEmpty, bool forceProvision, CancellationToken cancellationToken)
    {
        var client = connection.Client;
        IReadOnlyList<int> indices = [];
        if (!forceProvision)
        {
            await client.RescanBusAsync(cancellationToken).ConfigureAwait(false);
            indices = await client.GetDevicesMapAsync(cancellationToken).ConfigureAwait(false);
        }

        if (forceProvision || (provisionIfEmpty && indices.Count == 0))
        {
            await ProvisionAsync(client, cancellationToken).ConfigureAwait(false);
            await client.RescanBusAsync(cancellationToken).ConfigureAwait(false);
            indices = await client.GetDevicesMapAsync(cancellationToken).ConfigureAwait(false);
        }

        var retryDelay = Options.RetryDelay;
        var links = new Dictionary<string, DialLink>(StringComparer.OrdinalIgnoreCase);
        foreach (var index in indices)
        {
            string uid;
            try
            {
                uid = await client.GetUidAsync(index, cancellationToken).ConfigureAwait(false);
            }
            catch (VuHubException ex)
            {
                _logger.LogWarning("Cannot read the UID of the dial at bus index {Index}: {Message}", index, ex.Message);
                continue;
            }

            if (links.ContainsKey(uid))
            {
                _logger.LogWarning("Two dials report UID {Uid}; ignoring the one at bus index {Index}", uid, index);
                continue;
            }

            if (connection.Links.TryGetValue(uid, out var link))
            {
                if (link.BusIndex != index)
                {
                    _logger.LogInformation("Dial {Uid} moved from bus index {Old} to {New}", uid, link.BusIndex, index);
                    link.BusIndex = index;
                }

                if (link.Unresponsive)
                {
                    _logger.LogInformation("Dial {Uid} is back on the bus", uid);
                    link.Revive(retryDelay);
                }
            }
            else
            {
                link = new DialLink(uid, index, retryDelay);
                await ReadDialInfoAsync(client, link, cancellationToken).ConfigureAwait(false);
            }

            links[uid] = link;
        }

        foreach (var gone in connection.Links.Values.Where(l => !links.ContainsKey(l.Uid)))
        {
            _logger.LogWarning("Dial {Uid} is no longer on the bus", gone.Uid);
            EndIdentify(gone);
        }

        connection.Links = links;
        connection.Upload = null;
        connection.LastScan = _time.GetTimestamp();
        PublishDials(connection);
    }

    private async Task ProvisionAsync(VuHubClient client, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Provisioning new dials");
        for (var attempt = 1; attempt <= ProvisionAttempts; attempt++)
        {
            try
            {
                await client.ProvisionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (VuHubException ex)
            {
                _logger.LogDebug("Provisioning attempt {Attempt} failed: {Message}", attempt, ex.Message);
            }

            await Task.Delay(ProvisionInterval, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReadDialInfoAsync(VuHubClient client, DialLink link, CancellationToken cancellationToken)
    {
        var index = link.BusIndex;
        link.FirmwareVersion = await TryReadAsync(link, "firmware version", () => client.GetFirmwareInfoAsync(index, cancellationToken)).ConfigureAwait(false);
        link.HardwareVersion = await TryReadAsync(link, "hardware version", () => client.GetHardwareInfoAsync(index, cancellationToken)).ConfigureAwait(false);
        link.ProtocolVersion = await TryReadAsync(link, "protocol version", () => client.GetProtocolInfoAsync(index, cancellationToken)).ConfigureAwait(false);
        link.BuildHash = await TryReadAsync(link, "build hash", () => client.GetBuildInfoAsync(index, cancellationToken)).ConfigureAwait(false);
        var easing = await TryReadAsync<VuEasingConfig?>(link, "easing", async () => await client.GetEasingConfigAsync(index, cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);
        if (easing is { } config)
        {
            link.AcceptEasing(config.Needle, backlight: false);
            link.AcceptEasing(config.Backlight, backlight: true);
        }
    }

    private async Task<T?> TryReadAsync<T>(DialLink link, string what, Func<Task<T>> read)
    {
        try
        {
            var value = await read().ConfigureAwait(false);
            return value is string { Length: 0 } ? default : value;
        }
        catch (VuHubException ex)
        {
            _logger.LogDebug("Reading the {What} of dial {Uid} failed: {Message}", what, link.Uid, ex.Message);
            return default;
        }
    }

    private void ReportFailure(Exception error, Connection? connection, int failures)
    {
        var port = connection?.PortName ?? _attemptedPort;
        var simulated = connection?.Simulated ?? (Options.Simulate > 0 && _transportFactory is null);
        var lost = connection is not null && error is IOException;
        var (state, message) = error switch
        {
            HubNotFoundException => (HubConnectionState.Disconnected, error.Message),
            IOException when lost => (HubConnectionState.Disconnected, $"Connection lost: {error.Message}"),
            UnauthorizedAccessException => (HubConnectionState.Error, AccessDeniedMessage(port)),
            VuHubException => (HubConnectionState.Error, $"The hub on {port} does not respond: {error.Message}"),
            _ => (HubConnectionState.Error, error.Message),
        };

        SetStatus(new HubStatus(state, error is HubNotFoundException ? null : port, message, simulated));
        if (lost || failures == 1)
        {
            if (error is HubNotFoundException or UnauthorizedAccessException || lost)
            {
                _logger.LogWarning("VU1 hub: {Message}; retrying", message);
            }
            else
            {
                _logger.LogWarning(error, "VU1 hub: {Message}; retrying", message);
            }
        }
        else
        {
            _logger.LogDebug("VU1 hub: {Message} (attempt {Attempt})", message, failures);
        }
    }

    private static string AccessDeniedMessage(string? port) => OperatingSystem.IsWindows()
        ? $"{port} is in use by another program. Stop VU-Server, which cannot share the port."
        : $"Access to {port} was denied. It may be in use (stop VU-Server), or the user needs the 'dialout' group or a udev rule for 0403:6015.";

    private async Task CloseAsync(Connection connection)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_connection, connection))
            {
                _connection = null;
            }
        }

        AbandonWork();
        foreach (var link in connection.Links.Values)
        {
            EndIdentify(link);
        }

        connection.Upload = null;
        PublishDials(null);
        await DisposeQuietlyAsync(connection, connection.Transport).ConfigureAwait(false);
    }

    private async Task DisposeQuietlyAsync(Connection? connection, IVuTransport transport)
    {
        try
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Closing {Port} failed", connection?.PortName ?? transport.Name);
        }
    }

    /// <summary>Publishes <see cref="Dials"/>: the connection's dials plus every dial seen before, offline.</summary>
    private void PublishDials(Connection? connection)
    {
        foreach (var uid in _known.Keys.ToList())
        {
            if (connection is null || !connection.Links.ContainsKey(uid))
            {
                _known[uid] = _known[uid] with { Online = false };
            }
        }

        foreach (var link in connection?.Links.Values ?? Enumerable.Empty<DialLink>())
        {
            _known[link.Uid] = link.ToInfo();
        }

        DialInfo[] dials = [.. _known.Values.OrderBy(d => d.BusIndex).ThenBy(d => d.Uid, StringComparer.Ordinal)];
        if (dials.SequenceEqual(Dials))
        {
            return;
        }

        Volatile.Write(ref _dials, dials);
        RaiseChanged();
    }

    /// <param name="PortName">The serial port, or null for the simulator and injected transports.</param>
    private readonly record struct TransportCandidate(string? PortName, Func<IVuTransport> Create, bool Probe);

    private sealed class HubNotFoundException() : Exception("VU1 hub not found. Check the USB cable, or set the port manually.");

    /// <summary>One open link to the hub and the dials found on it. Touched only by the run loop.</summary>
    private sealed class Connection(IVuTransport transport, VuHubClient client, string portName, bool simulated, int invalidation) : IAsyncDisposable
    {
        private Exception? _lost;

        public IVuTransport Transport { get; } = transport;

        public VuHubClient Client { get; } = client;

        /// <summary>The port shown in <see cref="HubStatus.Port"/>.</summary>
        public string PortName { get; } = portName;

        public bool Simulated { get; } = simulated;

        /// <summary>Dials on the bus by UID; replaced (never mutated) by a rescan.</summary>
        public Dictionary<string, DialLink> Links { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public FaceUpload? Upload { get; set; }

        /// <summary>Bus index of the last face upload, for round-robin.</summary>
        public int LastFaceIndex { get; set; } = -1;

        /// <summary>The <see cref="DialController._invalidation"/> count already applied.</summary>
        public int Invalidation { get; set; } = invalidation;

        public long LastScan { get; set; }

        /// <summary>Earliest timestamp at which something becomes due; reset every cycle.</summary>
        public long NextWake { get; set; }

        /// <summary>True when the current cycle sent anything.</summary>
        public bool Progressed { get; set; }

        public Exception? Lost => Volatile.Read(ref _lost);

        public void MarkLost(Exception error) => Interlocked.CompareExchange(ref _lost, error, null);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Transport.DisposeAsync().ConfigureAwait(false);
        }
    }
}
