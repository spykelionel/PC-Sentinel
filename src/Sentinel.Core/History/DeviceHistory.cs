using System.Globalization;
using System.Text.Json;
using Sentinel.Core.Audit;
using Sentinel.Core.Devices;

namespace Sentinel.Core.History;

public enum ConnectionEndReason
{
    Removed = 1,
    MediaChanged = 2,

    /// <summary>The service stopped cleanly while the device was connected.</summary>
    ServiceStopped = 3,

    /// <summary>Found open at startup: the previous service run ended without closing it (crash, power loss).</summary>
    ServiceRestart = 4,
}

/// <summary>One persistent change. Changes are applied atomically, in order, together with their audit entries.</summary>
public abstract record HistoryChange(DateTimeOffset OccurredAt);

public sealed record DeviceLifecycleChange(DeviceLifecycleEvent Event) : HistoryChange(Event.OccurredAt);

/// <summary>A service-level audit record that has no device data of its own (e.g. service started).</summary>
public sealed record AuditOnlyChange(AuditRecord Record) : HistoryChange(Record.OccurredAt);

/// <summary>Ends every connection still marked open. Used at startup (ServiceRestart) and at shutdown (ServiceStopped).</summary>
public sealed record CloseOpenConnectionsChange(DateTimeOffset OccurredAt, ConnectionEndReason Reason) : HistoryChange(OccurredAt);

/// <summary>A row of connection history, for diagnostics and (later) the dashboard.</summary>
public sealed record ConnectionHistoryEntry(
    Guid ConnectionId,
    string FingerprintKey,
    IdentityConfidence Confidence,
    string? Model,
    string StorageInstanceId,
    DateTimeOffset ConnectedAt,
    DateTimeOffset? DisconnectedAt,
    ConnectionEndReason? EndReason);

/// <summary>Durable device history and audit trail. Implementations must apply each batch in a single transaction.</summary>
public interface IHistoryStore
{
    /// <summary>Creates or migrates the database. Safe to call repeatedly.</summary>
    void Initialize();

    void Apply(IReadOnlyList<HistoryChange> changes);

    AuditVerificationResult VerifyAuditChain();

    /// <summary>Deletes history older than <paramref name="cutoff"/>, preserving audit-chain verifiability. Returns audit entries removed.</summary>
    long Purge(DateTimeOffset cutoff);

    IReadOnlyList<ConnectionHistoryEntry> GetRecentConnections(int count);
}

/// <summary>Maps lifecycle events to audit records. Details are JSON; device strings are already sanitized.</summary>
public static class AuditRecords
{
    public static AuditRecord From(DeviceLifecycleEvent lifecycleEvent)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);

        return lifecycleEvent switch
        {
            DeviceConnected e => new AuditRecord(e.OccurredAt, AuditEventType.DeviceConnected, e.ConnectionId, e.Disk.Fingerprint.Key,
                Json(new()
                {
                    ["storageInstanceId"] = e.Disk.Device.StorageInstanceId.Value,
                    ["usbParentInstanceId"] = e.Disk.Device.UsbParentInstanceId?.Value,
                    ["model"] = e.Disk.Device.Model ?? e.Disk.Device.FriendlyName,
                    ["busType"] = e.Disk.Device.BusType.ToString(),
                    ["confidence"] = e.Disk.Fingerprint.Confidence.ToString(),
                    ["hardwareSerial"] = e.Disk.Fingerprint.HardwareSerial,
                    ["sizeBytes"] = e.Disk.Device.SizeBytes.ToString(CultureInfo.InvariantCulture),
                    ["volumes"] = string.Join(";", e.Disk.Volumes.Select(v => v.VolumeGuidPath)),
                })),
            DeviceDisconnected e => new AuditRecord(e.OccurredAt, AuditEventType.DeviceDisconnected, e.ConnectionId, e.LastKnown.Fingerprint.Key,
                Json(new() { ["reason"] = e.Reason.ToString() })),
            VolumeMounted e => new AuditRecord(e.OccurredAt, AuditEventType.VolumeMounted, e.ConnectionId, null, Volume(e.Volume)),
            VolumeDismounted e => new AuditRecord(e.OccurredAt, AuditEventType.VolumeDismounted, e.ConnectionId, null, Volume(e.Volume)),
            VolumeUpdated e => new AuditRecord(e.OccurredAt, AuditEventType.VolumeUpdated, e.ConnectionId, null,
                Json(new()
                {
                    ["volumeGuidPath"] = e.Current.VolumeGuidPath,
                    ["previousDriveLetter"] = e.Previous.DriveLetter?.ToString(),
                    ["driveLetter"] = e.Current.DriveLetter?.ToString(),
                    ["previousLabel"] = e.Previous.Label,
                    ["label"] = e.Current.Label,
                })),
            _ => throw new ArgumentOutOfRangeException(nameof(lifecycleEvent), lifecycleEvent.GetType().Name, "Unknown lifecycle event."),
        };
    }

    public static string Json(Dictionary<string, string?> details) => JsonSerializer.Serialize(details);

    private static string Volume(VolumeInfo volume) =>
        Json(new()
        {
            ["volumeGuidPath"] = volume.VolumeGuidPath,
            ["driveLetter"] = volume.DriveLetter?.ToString(),
            ["label"] = volume.Label,
            ["fileSystem"] = volume.FileSystem,
            ["volumeSerial"] = volume.SerialNumber?.ToString("X8", CultureInfo.InvariantCulture),
        });
}
