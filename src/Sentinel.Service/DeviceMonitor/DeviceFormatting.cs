using System.Globalization;
using Sentinel.Core.Devices;

namespace Sentinel.Service.DeviceMonitor;

/// <summary>Human-readable descriptions of devices for logs and the diagnostic listing.</summary>
public static class DeviceFormatting
{
    public static string DescribeVolume(VolumeInfo volume)
    {
        ArgumentNullException.ThrowIfNull(volume);

        var letter = volume.DriveLetter is { } l ? $"{l}:" : "(no letter)";
        var serial = volume.SerialNumber?.ToString("X8", CultureInfo.InvariantCulture) ?? "-";
        return $"{letter} {volume.FileSystem ?? "?"} label='{volume.Label}' serial={serial} capacity={volume.CapacityBytes} {volume.VolumeGuidPath}";
    }

    public static string DescribeVolumes(IReadOnlyList<VolumeInfo> volumes)
    {
        ArgumentNullException.ThrowIfNull(volumes);

        return volumes.Count == 0 ? "(none mounted)" : string.Join(" | ", volumes.Select(DescribeVolume));
    }

    public static string DescribeDisk(ExternalDisk disk)
    {
        ArgumentNullException.ThrowIfNull(disk);

        var device = disk.Device;
        var fingerprint = disk.Fingerprint;
        var vidPid = fingerprint.VendorId is { } vid && fingerprint.ProductId is { } pid
            ? string.Create(CultureInfo.InvariantCulture, $"{vid:X4}:{pid:X4}")
            : "(unknown)";

        return string.Join(Environment.NewLine,
            $"Disk {device.DiskNumber}: {device.Model ?? device.FriendlyName}",
            $"  Bus type:         {device.BusType}",
            $"  Size:             {device.SizeBytes} bytes",
            $"  Storage instance: {device.StorageInstanceId}",
            $"  USB parent:       {device.UsbParentInstanceId?.Value ?? "(none)"}",
            $"  VID:PID:          {vidPid}",
            $"  Container ID:     {device.ContainerId?.ToString() ?? "(none)"}",
            $"  Descriptor:       {fingerprint.Descriptor?.Vendor} / {fingerprint.Descriptor?.Product} / {fingerprint.Descriptor?.Revision}",
            $"  Reported serial:  {device.ReportedSerialNumber ?? "(none)"}",
            $"  Hardware serial:  {fingerprint.HardwareSerial ?? "(none - Windows-generated instance ID)"}",
            $"  Confidence:       {fingerprint.Confidence}",
            $"  Partition table:  {device.PartitionTableId ?? "(none)"}",
            $"  Fingerprint:      {fingerprint.Key}",
            $"  Volumes:          {DescribeVolumes(disk.Volumes)}");
    }
}
