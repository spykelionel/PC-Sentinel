namespace Sentinel.Core.Devices;

public enum DisconnectReason
{
    /// <summary>The disk is no longer present.</summary>
    Removed = 1,

    /// <summary>
    /// The same disk now reports different media (card-reader swap, repartition). Treated as a new
    /// connection so that a new authorization is required.
    /// </summary>
    MediaChanged = 2,
}

public abstract record DeviceLifecycleEvent(Guid ConnectionId, DateTimeOffset OccurredAt);

public sealed record DeviceConnected(Guid ConnectionId, DateTimeOffset OccurredAt, ExternalDisk Disk)
    : DeviceLifecycleEvent(ConnectionId, OccurredAt);

public sealed record DeviceDisconnected(Guid ConnectionId, DateTimeOffset OccurredAt, ExternalDisk LastKnown, DisconnectReason Reason)
    : DeviceLifecycleEvent(ConnectionId, OccurredAt);

public sealed record VolumeMounted(Guid ConnectionId, DateTimeOffset OccurredAt, VolumeInfo Volume)
    : DeviceLifecycleEvent(ConnectionId, OccurredAt);

public sealed record VolumeDismounted(Guid ConnectionId, DateTimeOffset OccurredAt, VolumeInfo Volume)
    : DeviceLifecycleEvent(ConnectionId, OccurredAt);

/// <summary>Volume properties changed without a remount, e.g. a drive-letter or label change.</summary>
public sealed record VolumeUpdated(Guid ConnectionId, DateTimeOffset OccurredAt, VolumeInfo Previous, VolumeInfo Current)
    : DeviceLifecycleEvent(ConnectionId, OccurredAt);

/// <summary>One continuous presence of a physical device with the same media.</summary>
public sealed record DeviceConnection(Guid ConnectionId, DateTimeOffset ConnectedAt, ExternalDisk Disk);

/// <summary>
/// In-memory inventory of connected external disks, driven by full snapshots: each scan is reconciled
/// against the previous state, so missed, duplicated, or reordered WMI events are harmless.
/// Not thread-safe; owned by a single orchestration loop.
/// </summary>
public sealed class DeviceInventory(Func<Guid> newConnectionId)
{
    private readonly Dictionary<PnpInstanceId, DeviceConnection> _connections = [];

    public DeviceInventory()
        : this(Guid.NewGuid)
    {
    }

    public IReadOnlyCollection<DeviceConnection> Connections => _connections.Values;

    public IReadOnlyList<DeviceLifecycleEvent> Reconcile(IReadOnlyCollection<ExternalDisk> present, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(present);

        var events = new List<DeviceLifecycleEvent>();
        var presentById = new Dictionary<PnpInstanceId, ExternalDisk>();
        foreach (var disk in present)
        {
            presentById[disk.Device.StorageInstanceId] = disk;
        }

        foreach (var (id, connection) in _connections.ToList())
        {
            if (!presentById.TryGetValue(id, out var current))
            {
                _connections.Remove(id);
                events.Add(new DeviceDisconnected(connection.ConnectionId, now, connection.Disk, DisconnectReason.Removed));
            }
            else if (!IsSameMedia(connection.Disk, current))
            {
                _connections.Remove(id);
                events.Add(new DeviceDisconnected(connection.ConnectionId, now, connection.Disk, DisconnectReason.MediaChanged));
            }
        }

        foreach (var disk in presentById.Values)
        {
            var id = disk.Device.StorageInstanceId;
            if (_connections.TryGetValue(id, out var connection))
            {
                AddVolumeChanges(events, connection, disk, now);
                _connections[id] = connection with { Disk = disk };
            }
            else
            {
                var created = new DeviceConnection(newConnectionId(), now, disk);
                _connections[id] = created;
                events.Add(new DeviceConnected(created.ConnectionId, now, disk));
            }
        }

        return events;
    }

    private static bool IsSameMedia(ExternalDisk previous, ExternalDisk current) =>
        previous.Fingerprint.Key == current.Fingerprint.Key
        && previous.Device.SizeBytes == current.Device.SizeBytes;

    private static void AddVolumeChanges(List<DeviceLifecycleEvent> events, DeviceConnection connection, ExternalDisk current, DateTimeOffset now)
    {
        var previous = connection.Disk.Volumes.ToDictionary(v => v.VolumeGuidPath, StringComparer.OrdinalIgnoreCase);
        var next = current.Volumes.ToDictionary(v => v.VolumeGuidPath, StringComparer.OrdinalIgnoreCase);

        foreach (var (path, volume) in previous)
        {
            if (!next.ContainsKey(path))
            {
                events.Add(new VolumeDismounted(connection.ConnectionId, now, volume));
            }
        }

        foreach (var (path, volume) in next)
        {
            if (!previous.TryGetValue(path, out var old))
            {
                events.Add(new VolumeMounted(connection.ConnectionId, now, volume));
            }
            else if (old != volume)
            {
                events.Add(new VolumeUpdated(connection.ConnectionId, now, old, volume));
            }
        }
    }
}
