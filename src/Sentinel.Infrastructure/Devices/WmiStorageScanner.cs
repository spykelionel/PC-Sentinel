using System.Globalization;
using System.Management;
using Sentinel.Core.Devices;
using Sentinel.Infrastructure.Native;

namespace Sentinel.Infrastructure.Devices;

/// <summary>
/// Enumerates external storage by joining four read-only WMI views:
/// Win32_Volume (volume GUID path, letter, label, FS, serial) → MSFT_Partition (AccessPaths → disk number)
/// → MSFT_Disk (bus type, size, partition table) → Win32_DiskDrive (PnP instance ID),
/// then walks the PnP device tree (cfgmgr32) for the parent USB device and container ID.
/// </summary>
public sealed class WmiStorageScanner : IStorageScanner
{
    private const string StorageScope = @"\\.\root\Microsoft\Windows\Storage";
    private const string CimV2Scope = @"\\.\root\cimv2";
    private const int RemovableDriveType = 2;
    private const int MaxParentDepth = 3;

    public StorageScanResult Scan(CancellationToken cancellationToken)
    {
        var pnpIdsByDiskIndex = Query(
                CimV2Scope,
                "SELECT Index, PNPDeviceID, Model FROM Win32_DiskDrive",
                o => (Index: ToInt(o["Index"]), PnpId: o["PNPDeviceID"] as string, Model: o["Model"] as string))
            .Where(d => d.Index is not null)
            .GroupBy(d => d.Index!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        cancellationToken.ThrowIfCancellationRequested();

        var disks = Query(
            StorageScope,
            "SELECT Number, BusType, FriendlyName, SerialNumber, Size, PartitionStyle, Guid, Signature FROM MSFT_Disk",
            o => new StorageDiskRow(
                ToInt(o["Number"]),
                StorageBusClassification.FromRaw(ToInt(o["BusType"]) ?? 0),
                o["FriendlyName"] as string,
                o["SerialNumber"] as string,
                ToULong(o["Size"]) ?? 0,
                ToInt(o["PartitionStyle"]),
                o["Guid"] as string,
                ToULong(o["Signature"])));
        cancellationToken.ThrowIfCancellationRequested();

        var diskNumberByVolumePath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var partition in Query(
            StorageScope,
            "SELECT DiskNumber, AccessPaths FROM MSFT_Partition",
            o => (DiskNumber: ToInt(o["DiskNumber"]), AccessPaths: o["AccessPaths"] as string[])))
        {
            if (partition.DiskNumber is not { } diskNumber || partition.AccessPaths is null)
            {
                continue;
            }

            foreach (var path in partition.AccessPaths.Where(IsVolumeGuidPath))
            {
                diskNumberByVolumePath[path] = diskNumber;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();

        var volumes = Query(
            CimV2Scope,
            "SELECT DeviceID, DriveLetter, Label, FileSystem, SerialNumber, Capacity, DriveType FROM Win32_Volume",
            o => (Volume: ToVolumeInfo(o), DriveType: ToInt(o["DriveType"])))
            .Where(v => v.Volume is not null)
            .Select(v => (Volume: v.Volume!, v.DriveType))
            .ToList();

        var unresolved = new List<string>();
        var externalDisks = new List<ExternalDisk>();

        foreach (var disk in disks)
        {
            if (disk.Number is not { } number
                || !StorageBusClassification.IsExternalStorageBus(disk.BusType)
                || disk.SizeBytes == 0)
            {
                continue;
            }

            if (!pnpIdsByDiskIndex.TryGetValue(number, out var drive)
                || !PnpInstanceId.TryParse(drive.PnpId, out var instanceId))
            {
                unresolved.Add($"Disk {number}: PnP instance ID not yet available");
                continue;
            }

            var device = new PhysicalDeviceInfo(
                instanceId,
                FindUsbAncestor(instanceId),
                CfgMgr32.GetGuidProperty(instanceId.Value, CfgMgr32.DeviceContainerId),
                number,
                disk.BusType,
                DeviceStrings.Sanitize(disk.FriendlyName),
                DeviceStrings.Sanitize(drive.Model),
                DeviceStrings.Sanitize(disk.SerialNumber),
                disk.SizeBytes,
                disk.PartitionTableId);

            var diskVolumes = volumes
                .Where(v => diskNumberByVolumePath.TryGetValue(v.Volume.VolumeGuidPath, out var n) && n == number)
                .Select(v => v.Volume)
                .OrderBy(v => v.VolumeGuidPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            externalDisks.Add(new ExternalDisk(device, DeviceFingerprint.Create(device), diskVolumes));
        }

        foreach (var (volume, driveType) in volumes)
        {
            if (driveType == RemovableDriveType && !diskNumberByVolumePath.ContainsKey(volume.VolumeGuidPath))
            {
                unresolved.Add($"Volume {volume.VolumeGuidPath}: not yet mapped to a disk");
            }
        }

        return new StorageScanResult(externalDisks, unresolved);
    }

    /// <summary>Walks up the device tree to the nearest <c>USB\VID_…</c> node (USBSTOR and UASP disks).</summary>
    private static PnpInstanceId? FindUsbAncestor(PnpInstanceId instanceId)
    {
        var current = instanceId.Value;
        for (var depth = 0; depth < MaxParentDepth; depth++)
        {
            var parentValue = CfgMgr32.GetStringProperty(current, CfgMgr32.DeviceParent);
            if (!PnpInstanceId.TryParse(parentValue, out var parent))
            {
                return null;
            }

            if (parent.TryGetUsbVidPid(out _, out _))
            {
                return parent;
            }

            current = parent.Value;
        }

        return null;
    }

    private static VolumeInfo? ToVolumeInfo(ManagementBaseObject o)
    {
        var path = o["DeviceID"] as string;
        if (path is null || !IsVolumeGuidPath(path))
        {
            return null;
        }

        var letter = o["DriveLetter"] as string;
        return new VolumeInfo(
            path,
            letter is { Length: >= 1 } && char.IsAsciiLetter(letter[0]) ? char.ToUpperInvariant(letter[0]) : null,
            DeviceStrings.Sanitize(o["Label"] as string),
            DeviceStrings.Sanitize(o["FileSystem"] as string),
            ToULong(o["SerialNumber"]) is { } serial ? (uint)serial : null,
            ToULong(o["Capacity"]));
    }

    private static bool IsVolumeGuidPath(string path) =>
        path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase);

    private static List<T> Query<T>(string scope, string wql, Func<ManagementBaseObject, T> map)
    {
        using var searcher = new ManagementObjectSearcher(scope, wql);
        using var results = searcher.Get();
        var rows = new List<T>();
        foreach (var item in results)
        {
            using (item)
            {
                rows.Add(map(item));
            }
        }

        return rows;
    }

    private static int? ToInt(object? value) =>
        value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private static ulong? ToULong(object? value) =>
        value is null ? null : Convert.ToUInt64(value, CultureInfo.InvariantCulture);

    private sealed record StorageDiskRow(
        int? Number,
        StorageBusType BusType,
        string? FriendlyName,
        string? SerialNumber,
        ulong SizeBytes,
        int? PartitionStyle,
        string? GptGuid,
        ulong? MbrSignature)
    {
        private const int Mbr = 1;
        private const int Gpt = 2;

        public string? PartitionTableId => PartitionStyle switch
        {
            Gpt when GptGuid is not null => $"gpt:{GptGuid.Trim('{', '}')}",
            Mbr when MbrSignature is not null => $"mbr:{MbrSignature.Value:X8}",
            _ => null,
        };
    }
}
