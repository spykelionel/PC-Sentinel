using Sentinel.Core.Devices;
using Sentinel.Infrastructure.Devices;
using Sentinel.Infrastructure.Native;

namespace Sentinel.Infrastructure.Tests.Devices;

/// <summary>Read-only integration tests against the real WMI storage providers of this machine.</summary>
public sealed class WmiStorageScannerTests
{
    private readonly WmiStorageScanner _scanner = new();

    [Fact]
    public void Scan_CompletesWithoutError()
    {
        var result = _scanner.Scan(CancellationToken.None);

        Assert.NotNull(result.ExternalDisks);
        Assert.NotNull(result.UnresolvedItems);
    }

    [Fact]
    public void Scan_NeverReportsTheSystemVolume()
    {
        var systemDrive = char.ToUpperInvariant(Environment.GetFolderPath(Environment.SpecialFolder.Windows)[0]);

        var result = _scanner.Scan(CancellationToken.None);

        Assert.DoesNotContain(result.ExternalDisks.SelectMany(d => d.Volumes), v => v.DriveLetter == systemDrive);
    }

    [Fact]
    public void Scan_ReportsOnlyExternalBuses()
    {
        var result = _scanner.Scan(CancellationToken.None);

        Assert.All(result.ExternalDisks, d => Assert.True(StorageBusClassification.IsExternalStorageBus(d.Device.BusType)));
    }

    [Fact]
    public void Scan_HonoursCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => _scanner.Scan(cancelled.Token));
    }

    [Fact]
    public void CfgMgr32_ResolvesParentOfRootDevice_AndReturnsNullForUnknownDevice()
    {
        // HTREE\ROOT\0 is the root of every Windows device tree; its children report it as parent.
        Assert.Null(CfgMgr32.GetStringProperty(@"USBSTOR\DISK&VEN_NONE&PROD_NONE\0&0&0", CfgMgr32.DeviceParent));
        Assert.Null(CfgMgr32.GetGuidProperty(@"USBSTOR\DISK&VEN_NONE&PROD_NONE\0&0&0", CfgMgr32.DeviceContainerId));
        Assert.Equal(@"HTREE\ROOT\0", CfgMgr32.GetStringProperty(@"ROOT\VOLMGR\0000", CfgMgr32.DeviceParent), ignoreCase: true);
    }

    [HardwareFact]
    [Trait("Category", "Hardware")]
    public void Scan_WithUsbDeviceConnected_ResolvesFullIdentity()
    {
        var result = _scanner.Scan(CancellationToken.None);

        var disk = Assert.Single(result.ExternalDisks);
        Assert.Equal(StorageBusType.Usb, disk.Device.BusType);
        Assert.NotNull(disk.Device.UsbParentInstanceId);
        Assert.NotNull(disk.Fingerprint.VendorId);
        Assert.NotNull(disk.Device.ContainerId);
        var volume = Assert.Single(disk.Volumes);
        Assert.NotNull(volume.DriveLetter);
        Assert.NotNull(volume.SerialNumber);
    }
}
