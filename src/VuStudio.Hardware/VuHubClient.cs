using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VuStudio.Core.Lighting;
using VuStudio.Hardware.Protocol;
using VuStudio.Hardware.Transport;

namespace VuStudio.Hardware;

/// <summary>
/// Transaction-level API of the VU1 hub over an <see cref="IVuTransport"/> (which it does not own).
/// Commands run one at a time: stale input is discarded before each write, then lines are read until a reply
/// whose command echo matches; other lines and replies to earlier commands are skipped.
/// </summary>
/// <remarks>
/// Failures: <see cref="VuStatusException"/> for a non-OK status, <see cref="VuTimeoutException"/> when no reply
/// arrives, <see cref="VuHubException"/> for an unusable reply, and <see cref="IOException"/> when the link is lost.
/// Bus indices are one byte; percent and backlight levels are 0–100.
/// </remarks>
public sealed class VuHubClient : IDisposable
{
    /// <summary>Raw DAC values are 16-bit.</summary>
    public const int MaxRawValue = ushort.MaxValue;

    private readonly IVuTransport _transport;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VuTimeouts _timeouts;

    public VuHubClient(IVuTransport transport, VuTimeouts? timeouts = null, ILogger? logger = null, TimeProvider? time = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _timeouts = timeouts ?? VuTimeouts.Default;
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    public IVuTransport Transport => _transport;

    /// <summary>Timeouts of the typed methods; may be replaced at any time.</summary>
    public VuTimeouts Timeouts
    {
        get => Volatile.Read(ref _timeouts);
        set => Volatile.Write(ref _timeouts, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>Sends one command and returns the matching reply.</summary>
    /// <exception cref="VuStatusException">The reply is a status other than OK.</exception>
    /// <exception cref="VuTimeoutException">No matching reply within <paramref name="timeout"/>.</exception>
    /// <exception cref="IOException">The link is closed or lost.</exception>
    public async Task<VuResponse> SendAsync(VuCommand command, VuDataType dataType, ReadOnlyMemory<byte> payload, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        var request = VuProtocol.EncodeRequest(command, dataType, payload.Span);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _transport.DiscardInput();
            _logger.LogTrace("VU1 > {Request}", request);
            await _transport.WriteLineAsync(request, cancellationToken).ConfigureAwait(false);

            var started = _time.GetTimestamp();
            while (true)
            {
                var remaining = timeout - _time.GetElapsedTime(started);
                var line = remaining > TimeSpan.Zero
                    ? await _transport.ReadLineAsync(remaining, cancellationToken).ConfigureAwait(false)
                    : null;
                if (line is null)
                {
                    throw new VuTimeoutException(command, timeout);
                }

                if (!VuResponse.TryParse(line, out var response))
                {
                    _logger.LogTrace("VU1 ignoring line '{Line}'", line);
                    continue;
                }

                if (response.Command != command)
                {
                    _logger.LogDebug("VU1 skipping reply {Reply} while waiting for {Command}", line, command);
                    continue;
                }

                _logger.LogTrace("VU1 < {Reply}", line);
                if (response.Status is { } status && status != VuStatus.Ok)
                {
                    throw new VuStatusException(command, status);
                }

                return response;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task RescanBusAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(VuCommand.RescanBus, [], Timeouts.Bus, cancellationToken);

    /// <summary>The bus indices whose slot in the device map is <c>01</c>.</summary>
    public async Task<IReadOnlyList<int>> GetDevicesMapAsync(CancellationToken cancellationToken = default)
    {
        var map = await QueryAsync(VuCommand.GetDevicesMap, [], Timeouts.Command, cancellationToken, statusMeansEmpty: true).ConfigureAwait(false);
        var present = new List<int>();
        for (var i = 0; i < map.Length; i++)
        {
            if (map[i] == 0x01)
            {
                present.Add(i);
            }
        }

        return present;
    }

    /// <summary>The dial's UID as uppercase hex (24 characters for the 12-byte UID).</summary>
    public async Task<string> GetUidAsync(int index, CancellationToken cancellationToken = default)
    {
        var uid = await QueryAsync(VuCommand.GetDeviceUid, [Index(index)], Timeouts.Command, cancellationToken).ConfigureAwait(false);
        return uid.Length > 0
            ? Convert.ToHexString(uid)
            : throw new VuHubException(VuCommand.GetDeviceUid, $"The dial at bus index {index} returned an empty UID.");
    }

    public Task ProvisionAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(VuCommand.ProvisionDevice, [], Timeouts.Bus, cancellationToken);

    public Task ResetAllAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(VuCommand.ResetAllDevices, [], Timeouts.Bus, cancellationToken);

    /// <summary>Switches power to all dials on or off.</summary>
    public Task DialPowerAsync(bool on, CancellationToken cancellationToken = default) =>
        ExecuteAsync(VuCommand.DialPower, [on ? (byte)1 : (byte)0], Timeouts.Command, cancellationToken);

    public Task SetPercentAsync(int index, int percent, CancellationToken cancellationToken = default) =>
        ExecuteAsync(VuCommand.SetDialPercentSingle, [Index(index), Level(percent)], Timeouts.Value, cancellationToken);

    public Task SetPercentMultipleAsync(IReadOnlyCollection<(int Index, int Percent)> values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            throw new ArgumentException("At least one value is required.", nameof(values));
        }

        var payload = new byte[values.Count * 2];
        var i = 0;
        foreach (var (index, percent) in values)
        {
            payload[i++] = Index(index);
            payload[i++] = Level(percent);
        }

        return ExecuteAsync(VuCommand.SetDialPercentMultiple, payload, Timeouts.Value, cancellationToken);
    }

    /// <summary>Moves the needle by raw DAC value (0–65535), bypassing the calibration.</summary>
    public Task SetRawAsync(int index, int raw, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(raw);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(raw, MaxRawValue);
        return ExecuteAsync(VuCommand.SetDialRawSingle, [Index(index), (byte)(raw >> 8), (byte)raw], Timeouts.Value, cancellationToken);
    }

    /// <summary>Stores the raw value that corresponds to 100 %.</summary>
    public Task CalibrateMaxAsync(int index, int raw, CancellationToken cancellationToken = default) =>
        ExecuteAsync(VuCommand.SetDialCalibrateMax, IndexAndU32(index, Raw(raw)), Timeouts.Command, cancellationToken);

    /// <summary>Stores the raw value that corresponds to 50 %.</summary>
    public Task CalibrateHalfAsync(int index, int raw, CancellationToken cancellationToken = default) =>
        ExecuteAsync(VuCommand.SetDialCalibrateHalf, IndexAndU32(index, Raw(raw)), Timeouts.Command, cancellationToken);

    /// <summary>Sets the backlight; each channel 0–100.</summary>
    public Task SetBacklightAsync(int index, int red, int green, int blue, int white, CancellationToken cancellationToken = default) =>
        ExecuteAsync(VuCommand.SetRgbBacklight, [Index(index), Level(red), Level(green), Level(blue), Level(white)], Timeouts.Value, cancellationToken);

    public Task SetBacklightAsync(int index, LedLevels levels, CancellationToken cancellationToken = default) =>
        SetBacklightAsync(index, levels.R, levels.G, levels.B, levels.W, cancellationToken);

    /// <summary>Needle easing: the needle moves <paramref name="step"/> % per period.</summary>
    public Task SetNeedleEasingStepAsync(int index, int step, CancellationToken cancellationToken = default) =>
        ExecuteAsync(VuCommand.SetDialEasingStep, IndexAndU32(index, step), Timeouts.Command, cancellationToken);

    public Task SetNeedleEasingPeriodAsync(int index, int periodMs, CancellationToken cancellationToken = default) =>
        ExecuteAsync(VuCommand.SetDialEasingPeriod, IndexAndU32(index, periodMs), Timeouts.Command, cancellationToken);

    /// <summary>Backlight easing: the light moves <paramref name="step"/> % per period.</summary>
    public Task SetBacklightEasingStepAsync(int index, int step, CancellationToken cancellationToken = default) =>
        ExecuteAsync(VuCommand.SetBacklightEasingStep, IndexAndU32(index, step), Timeouts.Command, cancellationToken);

    public Task SetBacklightEasingPeriodAsync(int index, int periodMs, CancellationToken cancellationToken = default) =>
        ExecuteAsync(VuCommand.SetBacklightEasingPeriod, IndexAndU32(index, periodMs), Timeouts.Command, cancellationToken);

    public async Task<VuEasingConfig> GetEasingConfigAsync(int index, CancellationToken cancellationToken = default)
    {
        var data = await QueryAsync(VuCommand.GetEasingConfig, [Index(index)], Timeouts.Command, cancellationToken).ConfigureAwait(false);
        if (data.Length < 16)
        {
            throw new VuHubException(VuCommand.GetEasingConfig, $"Expected 16 bytes of easing config, got {data.Length}.");
        }

        return new VuEasingConfig(
            BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0)),
            BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4)),
            BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8)),
            BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12)));
    }

    /// <summary>Firmware build hash.</summary>
    public Task<string> GetBuildInfoAsync(int index, CancellationToken cancellationToken = default) =>
        GetTextAsync(VuCommand.GetBuildInfo, index, cancellationToken);

    public Task<string> GetFirmwareInfoAsync(int index, CancellationToken cancellationToken = default) =>
        GetTextAsync(VuCommand.GetFirmwareInfo, index, cancellationToken);

    public Task<string> GetHardwareInfoAsync(int index, CancellationToken cancellationToken = default) =>
        GetTextAsync(VuCommand.GetHardwareInfo, index, cancellationToken);

    public Task<string> GetProtocolInfoAsync(int index, CancellationToken cancellationToken = default) =>
        GetTextAsync(VuCommand.GetProtocolInfo, index, cancellationToken);

    /// <summary>Fills the image buffer with white (or black) and resets the cursor.</summary>
    public Task DisplayClearAsync(int index, bool black = false, CancellationToken cancellationToken = default) =>
        ExecuteAsync(VuCommand.DisplayClear, [Index(index), black ? (byte)1 : (byte)0], Timeouts.Display, cancellationToken);

    public Task DisplayGotoXYAsync(int index, int x, int y, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(x, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(y, ushort.MaxValue);
        return ExecuteAsync(VuCommand.DisplayGotoXY, [Index(index), (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y], Timeouts.Display, cancellationToken);
    }

    /// <summary>Writes up to <see cref="VuProtocol.MaxImageChunk"/> encoded image bytes at the cursor.</summary>
    public Task DisplayImageDataAsync(int index, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty || data.Length > VuProtocol.MaxImageChunk)
        {
            throw new ArgumentOutOfRangeException(nameof(data), data.Length, $"An image frame holds 1–{VuProtocol.MaxImageChunk} bytes.");
        }

        var payload = new byte[data.Length + 1];
        payload[0] = Index(index);
        data.Span.CopyTo(payload.AsSpan(1));
        return ExecuteAsync(VuCommand.DisplayImageData, payload, Timeouts.Display, cancellationToken);
    }

    /// <summary>Shows the image buffer; the e-paper refresh takes a few seconds.</summary>
    public Task DisplayShowAsync(int index, CancellationToken cancellationToken = default) =>
        ExecuteAsync(VuCommand.DisplayShowImage, [Index(index)], Timeouts.Display, cancellationToken);

    public void Dispose() => _gate.Dispose();

    private Task ExecuteAsync(VuCommand command, byte[] payload, TimeSpan timeout, CancellationToken cancellationToken) =>
        SendAsync(command, VuProtocol.DefaultDataType(command), payload, timeout, cancellationToken);

    /// <summary>Sends a query and returns its data; a bare OK status is an error unless <paramref name="statusMeansEmpty"/>.</summary>
    private async Task<byte[]> QueryAsync(VuCommand command, byte[] payload, TimeSpan timeout, CancellationToken cancellationToken, bool statusMeansEmpty = false)
    {
        var response = await SendAsync(command, VuProtocol.DefaultDataType(command), payload, timeout, cancellationToken).ConfigureAwait(false);
        if (!response.IsStatus)
        {
            return response.Payload.ToArray();
        }

        return statusMeansEmpty ? [] : throw new VuHubException(command, $"{command} returned a status instead of data.");
    }

    private async Task<string> GetTextAsync(VuCommand command, int index, CancellationToken cancellationToken)
    {
        var data = await QueryAsync(command, [Index(index)], Timeouts.Command, cancellationToken, statusMeansEmpty: true).ConfigureAwait(false);
        var text = Encoding.ASCII.GetString(data).Trim('\0');
        var nul = text.IndexOf('\0', StringComparison.Ordinal);
        return (nul >= 0 ? text[..nul] : text).Trim();
    }

    private static byte[] IndexAndU32(int index, int value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value, name);
        var payload = new byte[5];
        payload[0] = Index(index);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1), (uint)value);
        return payload;
    }

    private static byte Index(int index, [CallerArgumentExpression(nameof(index))] string? name = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, byte.MaxValue, name);
        return (byte)index;
    }

    private static byte Level(int value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100, name);
        return (byte)value;
    }

    private static int Raw(int raw, [CallerArgumentExpression(nameof(raw))] string? name = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(raw, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(raw, MaxRawValue, name);
        return raw;
    }
}
