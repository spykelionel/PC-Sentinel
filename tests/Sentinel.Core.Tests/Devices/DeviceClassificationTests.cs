using Sentinel.Core.Devices;

namespace Sentinel.Core.Tests.Devices;

public sealed class DeviceClassificationTests
{
    [Theory]
    [InlineData(StorageBusType.Usb, true)]
    [InlineData(StorageBusType.Sd, true)]
    [InlineData(StorageBusType.Mmc, true)]
    [InlineData(StorageBusType.Nvme, false)]
    [InlineData(StorageBusType.Sata, false)]
    [InlineData(StorageBusType.Scsi, false)]
    [InlineData(StorageBusType.Virtual, false)]
    [InlineData(StorageBusType.Unknown, false)]
    public void IsExternalStorageBus(StorageBusType busType, bool expected)
    {
        Assert.Equal(expected, StorageBusClassification.IsExternalStorageBus(busType));
    }

    [Theory]
    [InlineData(7, StorageBusType.Usb)]
    [InlineData(17, StorageBusType.Nvme)]
    [InlineData(99, StorageBusType.Unknown)]
    [InlineData(-1, StorageBusType.Unknown)]
    public void FromRaw_MapsKnownValues_AndRejectsUnknown(int raw, StorageBusType expected)
    {
        Assert.Equal(expected, StorageBusClassification.FromRaw(raw));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  Afanyu Lionel ", "Afanyu Lionel")]
    [InlineData("bad\u0000name\r\n", "bad name")]
    public void Sanitize_StripsControlCharactersAndTrims(string? input, string? expected)
    {
        Assert.Equal(expected, DeviceStrings.Sanitize(input));
    }

    [Fact]
    public void Sanitize_CapsLength()
    {
        var sanitized = DeviceStrings.Sanitize(new string('x', 10_000));

        Assert.Equal(DeviceStrings.MaxLength, sanitized!.Length);
    }
}
