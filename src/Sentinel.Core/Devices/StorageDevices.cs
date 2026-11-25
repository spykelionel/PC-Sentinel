namespace Sentinel.Core.Devices;

/// <summary>A mounted file-system volume. The volume GUID path is its stable identity; drive letters are display data.</summary>
public sealed record VolumeInfo(
    string VolumeGuidPath,
    char? DriveLetter,
    string? Label,
    string? FileSystem,
    uint? SerialNumber,
    ulong? CapacityBytes);

/// <summary>A physical storage device (disk) as resolved from WMI and the PnP device tree.</summary>
public sealed record PhysicalDeviceInfo(
    PnpInstanceId StorageInstanceId,
    PnpInstanceId? UsbParentInstanceId,
    Guid? ContainerId,
    int DiskNumber,
    StorageBusType BusType,
    string? FriendlyName,
    string? Model,
    string? ReportedSerialNumber,
    ulong SizeBytes,
    string? PartitionTableId);

/// <summary>A connected external disk with its fingerprint and currently mounted volumes.</summary>
public sealed record ExternalDisk(
    PhysicalDeviceInfo Device,
    DeviceFingerprint Fingerprint,
    IReadOnlyList<VolumeInfo> Volumes);

/// <summary>Result of one enumeration of the system's storage.</summary>
/// <param name="ExternalDisks">Disks on an external bus (USB/SD/MMC) that have media.</param>
/// <param name="UnresolvedItems">
/// Volumes or disks that could not be fully resolved yet (typically delayed enumeration right after arrival).
/// A non-empty list means the scan should be retried.
/// </param>
public sealed record StorageScanResult(
    IReadOnlyList<ExternalDisk> ExternalDisks,
    IReadOnlyList<string> UnresolvedItems);

/// <summary>Enumerates currently present external storage. Implementations must be read-only.</summary>
public interface IStorageScanner
{
    StorageScanResult Scan(CancellationToken cancellationToken);
}
