using Sentinel.Core.Devices;

namespace Sentinel.Core.Tests.Devices;

public sealed class PnpInstanceIdTests
{
    // The reference device from the project brief: SMI USB DISK without a hardware serial.
    private const string ReferenceUsbStor = @"USBSTOR\DISK&VEN_SMI&PROD_USB_DISK&REV_1100\6&172B058&1";

    [Fact]
    public void TryParse_ReferenceDevice_SplitsSegments()
    {
        Assert.True(PnpInstanceId.TryParse(ReferenceUsbStor, out var id));

        Assert.Equal("USBSTOR", id.Enumerator);
        Assert.Equal("DISK&VEN_SMI&PROD_USB_DISK&REV_1100", id.DeviceId);
        Assert.Equal("6&172B058&1", id.InstanceSegment);
    }

    [Fact]
    public void ReferenceDevice_IsSystemGenerated_AndHasNoSerial()
    {
        var id = TestDevices.Id(ReferenceUsbStor);

        Assert.True(id.IsSystemGenerated);
        Assert.Null(id.GetDeviceSerial());
    }

    [Fact]
    public void ReferenceDevice_StorageDescriptor_IsParsed()
    {
        var id = TestDevices.Id(ReferenceUsbStor);

        Assert.Equal(new StorageDescriptor("SMI", "USB_DISK", "1100"), id.GetStorageDescriptor());
    }

    [Fact]
    public void UsbStorWithSerial_StripsLunSuffix()
    {
        var id = TestDevices.Id(@"USBSTOR\Disk&Ven_Kingston&Prod_DataTraveler_3.0&Rev_PMAP\60A44C3FACC9B141B9C30D9F&0");

        Assert.False(id.IsSystemGenerated);
        Assert.Equal("60A44C3FACC9B141B9C30D9F", id.GetDeviceSerial());
    }

    [Fact]
    public void UsbParentWithSerial_ParsesVidPidAndSerial()
    {
        var id = TestDevices.Id(@"USB\VID_0951&PID_1666\60A44C3FACC9B141B9C30D9F");

        Assert.True(id.TryGetUsbVidPid(out var vid, out var pid));
        Assert.Equal(0x0951, vid);
        Assert.Equal(0x1666, pid);
        Assert.Equal("60A44C3FACC9B141B9C30D9F", id.GetDeviceSerial());
    }

    [Fact]
    public void UsbParentWithGeneratedId_HasNoSerial()
    {
        var id = TestDevices.Id(@"USB\VID_090C&PID_1000\5&2A3B4C5D&0&3");

        Assert.True(id.TryGetUsbVidPid(out _, out _));
        Assert.True(id.IsSystemGenerated);
        Assert.Null(id.GetDeviceSerial());
    }

    [Fact]
    public void TryGetUsbVidPid_NonUsbEnumerator_ReturnsFalse()
    {
        var id = TestDevices.Id(ReferenceUsbStor);

        Assert.False(id.TryGetUsbVidPid(out _, out _));
    }

    [Fact]
    public void TryGetUsbVidPid_MalformedHex_ReturnsFalse()
    {
        var id = TestDevices.Id(@"USB\VID_ZZZZ&PID_1000\ABC");

        Assert.False(id.TryGetUsbVidPid(out _, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("USBSTOR")]
    [InlineData(@"USBSTOR\DISK")]
    [InlineData(@"USBSTOR\\6&1")]
    [InlineData(@"A\B\C\D")]
    public void TryParse_Malformed_ReturnsFalse(string? value)
    {
        Assert.False(PnpInstanceId.TryParse(value, out _));
    }

    [Fact]
    public void Equality_IsCaseInsensitive()
    {
        var upper = TestDevices.Id(ReferenceUsbStor);
        var lower = TestDevices.Id(ReferenceUsbStor.ToLowerInvariant());

        Assert.Equal(upper, lower);
        Assert.Equal(upper.GetHashCode(), lower.GetHashCode());
    }
}
