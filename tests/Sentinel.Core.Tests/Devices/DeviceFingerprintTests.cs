using Sentinel.Core.Devices;

namespace Sentinel.Core.Tests.Devices;

public sealed class DeviceFingerprintTests
{
    [Fact]
    public void ReferenceDevice_WithoutSerial_IsWeak()
    {
        var fingerprint = DeviceFingerprint.Create(TestDevices.Device());

        Assert.Equal(IdentityConfidence.Weak, fingerprint.Confidence);
        Assert.Null(fingerprint.HardwareSerial);
        Assert.Equal((ushort)0x090C, fingerprint.VendorId);
        Assert.Equal((ushort)0x1000, fingerprint.ProductId);
        Assert.StartsWith("v1:", fingerprint.Key, StringComparison.Ordinal);
    }

    [Fact]
    public void UsbParentWithSerial_IsStrong()
    {
        var device = TestDevices.Device(
            instanceId: @"USBSTOR\Disk&Ven_Kingston&Prod_DT&Rev_PMAP\60A44C3F&0",
            usbParent: @"USB\VID_0951&PID_1666\60A44C3F");

        var fingerprint = DeviceFingerprint.Create(device);

        Assert.Equal(IdentityConfidence.Strong, fingerprint.Confidence);
        Assert.Equal("60A44C3F", fingerprint.HardwareSerial);
    }

    [Fact]
    public void StorageSerialWithoutUsbParent_IsStrong()
    {
        var device = TestDevices.Device(
            instanceId: @"USBSTOR\Disk&Ven_Kingston&Prod_DT&Rev_PMAP\60A44C3F&0",
            usbParent: null);

        Assert.Equal(IdentityConfidence.Strong, DeviceFingerprint.Create(device).Confidence);
    }

    [Fact]
    public void Strong_Key_DoesNotDependOnPortOrSize()
    {
        var a = TestDevices.Device(instanceId: @"USBSTOR\Disk&Ven_K&Prod_DT&Rev_1\SER1&0", usbParent: @"USB\VID_0951&PID_1666\SER1", diskNumber: 1, sizeBytes: 1000);
        var b = TestDevices.Device(instanceId: @"USBSTOR\Disk&Ven_K&Prod_DT&Rev_1\SER1&0", usbParent: @"USB\VID_0951&PID_1666\SER1", diskNumber: 4, sizeBytes: 2000);

        Assert.Equal(DeviceFingerprint.Create(a).Key, DeviceFingerprint.Create(b).Key);
    }

    [Fact]
    public void Weak_Key_IsStableForSameDeviceOnDifferentPort()
    {
        // A different port gives Windows-generated IDs with different values; the weak key must not use them.
        var portA = TestDevices.Device(instanceId: @"USBSTOR\DISK&VEN_SMI&PROD_USB_DISK&REV_1100\6&172B058&1", usbParent: @"USB\VID_090C&PID_1000\5&AAAA&0&1");
        var portB = TestDevices.Device(instanceId: @"USBSTOR\DISK&VEN_SMI&PROD_USB_DISK&REV_1100\6&99FF00&0", usbParent: @"USB\VID_090C&PID_1000\5&BBBB&0&4");

        Assert.Equal(DeviceFingerprint.Create(portA).Key, DeviceFingerprint.Create(portB).Key);
    }

    [Fact]
    public void Weak_Key_ChangesWhenPartitionTableChanges()
    {
        var before = DeviceFingerprint.Create(TestDevices.Device(partitionTableId: "mbr:C3072E18"));
        var after = DeviceFingerprint.Create(TestDevices.Device(partitionTableId: "mbr:00000001"));

        Assert.NotEqual(before.Key, after.Key);
    }

    [Fact]
    public void Weak_And_Strong_Keys_NeverCollide_ForSameDeviceStrings()
    {
        var weak = DeviceFingerprint.Create(TestDevices.Device());
        var strong = DeviceFingerprint.Create(TestDevices.Device(usbParent: @"USB\VID_090C&PID_1000\SERIAL"));

        Assert.NotEqual(weak.Key, strong.Key);
    }

    [Fact]
    public void Key_IsCaseInsensitiveToInstanceIdCasing()
    {
        var upper = DeviceFingerprint.Create(TestDevices.Device(usbParent: @"USB\VID_090C&PID_1000\ABCDEF"));
        var lower = DeviceFingerprint.Create(TestDevices.Device(usbParent: @"usb\vid_090c&pid_1000\abcdef"));

        Assert.Equal(upper.Key, lower.Key);
    }
}
