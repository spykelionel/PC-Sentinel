using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Sentinel.Core.Devices;

public enum IdentityConfidence
{
    /// <summary>
    /// No unique hardware serial. The identity is derived from model strings, size, and partition table,
    /// so identical devices can collide and repartitioning changes it.
    /// </summary>
    Weak = 1,

    /// <summary>The device reports a serial number that Windows uses in its instance ID.</summary>
    Strong = 2,
}

/// <summary>
/// Best-effort identity of a physical device, used for inventory and history only.
/// It never grants access (ADR-006): authorization is per connection session.
/// </summary>
public sealed record DeviceFingerprint(
    string Key,
    IdentityConfidence Confidence,
    ushort? VendorId,
    ushort? ProductId,
    string? HardwareSerial,
    StorageDescriptor? Descriptor)
{
    private const string KeyVersion = "v1";

    /// <summary>
    /// Rules, in order:
    /// 1. USB parent instance ID with a device serial: Strong, key = VID + PID + serial.
    /// 2. Storage instance ID with a device serial: Strong, key = storage device ID + serial.
    /// 3. Otherwise: Weak, key = VID/PID (if known) + storage device ID + size + partition table ID.
    /// </summary>
    public static DeviceFingerprint Create(PhysicalDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        ushort? vid = null, pid = null;
        string? usbSerial = null;
        if (device.UsbParentInstanceId is { } parent && parent.TryGetUsbVidPid(out var v, out var p))
        {
            vid = v;
            pid = p;
            usbSerial = parent.GetDeviceSerial();
        }

        var descriptor = device.StorageInstanceId.GetStorageDescriptor();
        var storageSerial = device.StorageInstanceId.GetDeviceSerial();
        var storageDeviceId = device.StorageInstanceId.DeviceId.ToUpperInvariant();

        if (vid is not null && usbSerial is not null)
        {
            return new DeviceFingerprint(
                ComputeKey($"usb|{vid:X4}|{pid:X4}|{usbSerial.ToUpperInvariant()}"),
                IdentityConfidence.Strong, vid, pid, usbSerial, descriptor);
        }

        if (storageSerial is not null)
        {
            return new DeviceFingerprint(
                ComputeKey($"stor|{storageDeviceId}|{storageSerial.ToUpperInvariant()}"),
                IdentityConfidence.Strong, vid, pid, storageSerial, descriptor);
        }

        var material = string.Join('|',
            "weak",
            vid?.ToString("X4", CultureInfo.InvariantCulture) ?? "-",
            pid?.ToString("X4", CultureInfo.InvariantCulture) ?? "-",
            storageDeviceId,
            device.SizeBytes.ToString(CultureInfo.InvariantCulture),
            device.PartitionTableId?.ToUpperInvariant() ?? "-");

        return new DeviceFingerprint(ComputeKey(material), IdentityConfidence.Weak, vid, pid, null, descriptor);
    }

    private static string ComputeKey(string material)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return $"{KeyVersion}:{Convert.ToHexStringLower(hash)}";
    }
}
