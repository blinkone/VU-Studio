using VuStudio.Core.Config;
using VuStudio.Core.Lighting;

namespace VuStudio.Core.Engine;

/// <summary>Live state of one dial slot, for the UI preview and the API.</summary>
public sealed record DialStateSnapshot(
    int Slot,
    string? Uid,
    string? Name,
    bool Online,
    string? ProfileId,
    int PageIndex,
    int PageCount,
    string? PageId,
    string? PageName,
    string? SensorId,
    double? Value,
    string? ValueText,
    string Unit,
    string Title,
    double Needle,
    LedLevels Light,
    string PreviewColor,
    long FaceVersion,
    bool Assigned,
    bool AlarmActive);

/// <summary>Live state of the whole engine.</summary>
public sealed record EngineSnapshot(
    string? ActiveProfileId,
    string? ActiveProfileName,
    string? ManualProfileId,
    NightAction Night,
    bool Paused,
    IReadOnlyList<DialStateSnapshot> Dials,
    DateTimeOffset Time)
{
    public static EngineSnapshot Empty { get; } = new(null, null, null, NightAction.None, false, [], DateTimeOffset.MinValue);
}

/// <summary>A desktop notification requested by the engine (e.g. an alarm).</summary>
public sealed record EngineNotification(string Title, string Message, string? Icon, int Slot);

/// <summary>Receives notifications (the service forwards them to the tray app and the web UI).</summary>
public interface INotificationSink
{
    void Notify(EngineNotification notification);
}
