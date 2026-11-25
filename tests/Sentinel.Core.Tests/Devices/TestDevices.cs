using Sentinel.Core.Devices;

namespace Sentinel.Core.Tests.Devices;

/// <summary>Builders for device test data.</summary>
internal static class TestDevices
{
    public const string ReferenceInstanceId = @"USBSTOR\DISK&VEN_SMI&PROD_USB_DISK&REV_1100\6&172B058&1";

    public static PnpInstanceId Id(string value) =>
        PnpInstanceId.TryParse(value, out var id) ? id : throw new ArgumentException($"Invalid instance ID: {value}");

    public static PhysicalDeviceInfo Device(
        string instanceId = ReferenceInstanceId,
        string? usbParent = @"USB\VID_090C&PID_1000\5&2A3B4C5D&0&3",
        int diskNumber = 1,
        ulong sizeBytes = 2_000_000_000,
        string? partitionTableId = "mbr:C3072E18",
        StorageBusType busType = StorageBusType.Usb) =>
        new(
            Id(instanceId),
            usbParent is null ? null : Id(usbParent),
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            diskNumber,
            busType,
            "SMI USB DISK",
            "SMI USB DISK",
            null,
            sizeBytes,
            partitionTableId);

    public static VolumeInfo Volume(string guid = "aaaaaaaa-0000-0000-0000-000000000001", char? letter = 'D', string? label = "Afanyu Lionel") =>
        new($@"\\?\Volume{{{guid}}}\", letter, label, "NTFS", 0xD4F0A755, 1_990_000_000);

    public static ExternalDisk Disk(PhysicalDeviceInfo? device = null, params VolumeInfo[] volumes)
    {
        device ??= Device();
        return new ExternalDisk(device, DeviceFingerprint.Create(device), volumes);
    }
}
