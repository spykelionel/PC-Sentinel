using Sentinel.Core.Devices;

namespace Sentinel.Core.Tests.Devices;

public sealed class DeviceInventoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    private const string SecondInstanceId = @"USBSTOR\DISK&VEN_GENERIC&PROD_FLASH&REV_1.00\7&1A2B3C&0";

    private int _nextId;
    private readonly DeviceInventory _inventory;

    public DeviceInventoryTests()
    {
        _inventory = new DeviceInventory(() => new Guid(++_nextId, 0, 0, new byte[8]));
    }

    [Fact]
    public void FirstScan_WithDevice_EmitsConnected()
    {
        var disk = TestDevices.Disk(volumes: TestDevices.Volume());

        var events = _inventory.Reconcile([disk], T0);

        var connected = Assert.IsType<DeviceConnected>(Assert.Single(events));
        Assert.Equal(disk, connected.Disk);
        Assert.Single(_inventory.Connections);
    }

    [Fact]
    public void RepeatedScan_WithSameState_EmitsNothing()
    {
        var disk = TestDevices.Disk(volumes: TestDevices.Volume());
        _inventory.Reconcile([disk], T0);

        Assert.Empty(_inventory.Reconcile([TestDevices.Disk(volumes: TestDevices.Volume())], T0.AddSeconds(60)));
    }

    [Fact]
    public void DeviceAbsent_EmitsDisconnectedRemoved()
    {
        var connected = (DeviceConnected)_inventory.Reconcile([TestDevices.Disk(volumes: TestDevices.Volume())], T0)[0];

        var events = _inventory.Reconcile([], T0.AddSeconds(5));

        var disconnected = Assert.IsType<DeviceDisconnected>(Assert.Single(events));
        Assert.Equal(connected.ConnectionId, disconnected.ConnectionId);
        Assert.Equal(DisconnectReason.Removed, disconnected.Reason);
        Assert.Empty(_inventory.Connections);
    }

    [Fact]
    public void Reconnect_CreatesNewConnectionId()
    {
        var first = (DeviceConnected)_inventory.Reconcile([TestDevices.Disk(volumes: TestDevices.Volume())], T0)[0];
        _inventory.Reconcile([], T0.AddSeconds(1));

        var second = (DeviceConnected)_inventory.Reconcile([TestDevices.Disk(volumes: TestDevices.Volume())], T0.AddSeconds(2))[0];

        Assert.NotEqual(first.ConnectionId, second.ConnectionId);
    }

    [Fact]
    public void TwoDevices_AreTrackedIndependently()
    {
        var a = TestDevices.Disk(volumes: TestDevices.Volume());
        var b = TestDevices.Disk(TestDevices.Device(instanceId: SecondInstanceId, diskNumber: 2), TestDevices.Volume("bbbbbbbb-0000-0000-0000-000000000002", 'E'));
        var events = _inventory.Reconcile([a, b], T0);
        Assert.Equal(2, events.OfType<DeviceConnected>().Count());

        var removal = _inventory.Reconcile([b], T0.AddSeconds(3));

        var disconnected = Assert.IsType<DeviceDisconnected>(Assert.Single(removal));
        Assert.Equal(a.Device.StorageInstanceId, disconnected.LastKnown.Device.StorageInstanceId);
        Assert.Equal(b.Device.StorageInstanceId, Assert.Single(_inventory.Connections).Disk.Device.StorageInstanceId);
    }

    [Fact]
    public void DriveLetterChange_EmitsVolumeUpdated_NotReconnect()
    {
        var connected = (DeviceConnected)_inventory.Reconcile([TestDevices.Disk(volumes: TestDevices.Volume(letter: 'D'))], T0)[0];

        var events = _inventory.Reconcile([TestDevices.Disk(volumes: TestDevices.Volume(letter: 'F'))], T0.AddSeconds(1));

        var updated = Assert.IsType<VolumeUpdated>(Assert.Single(events));
        Assert.Equal(connected.ConnectionId, updated.ConnectionId);
        Assert.Equal('D', updated.Previous.DriveLetter);
        Assert.Equal('F', updated.Current.DriveLetter);
    }

    [Fact]
    public void DelayedVolumeMount_EmitsVolumeMountedOnExistingConnection()
    {
        var connected = (DeviceConnected)_inventory.Reconcile([TestDevices.Disk()], T0)[0];
        Assert.Empty(connected.Disk.Volumes);

        var events = _inventory.Reconcile([TestDevices.Disk(volumes: TestDevices.Volume())], T0.AddSeconds(2));

        var mounted = Assert.IsType<VolumeMounted>(Assert.Single(events));
        Assert.Equal(connected.ConnectionId, mounted.ConnectionId);
    }

    [Fact]
    public void VolumeRemovedWhileDiskPresent_EmitsVolumeDismounted()
    {
        _inventory.Reconcile([TestDevices.Disk(volumes: TestDevices.Volume())], T0);

        var events = _inventory.Reconcile([TestDevices.Disk()], T0.AddSeconds(1));

        Assert.IsType<VolumeDismounted>(Assert.Single(events));
    }

    [Fact]
    public void MediaChange_EndsConnectionAndStartsNewOne()
    {
        var first = (DeviceConnected)_inventory.Reconcile([TestDevices.Disk(TestDevices.Device(sizeBytes: 2_000_000_000))], T0)[0];

        var events = _inventory.Reconcile([TestDevices.Disk(TestDevices.Device(sizeBytes: 32_000_000_000))], T0.AddSeconds(1));

        Assert.Collection(
            events,
            e =>
            {
                var disconnected = Assert.IsType<DeviceDisconnected>(e);
                Assert.Equal(first.ConnectionId, disconnected.ConnectionId);
                Assert.Equal(DisconnectReason.MediaChanged, disconnected.Reason);
            },
            e => Assert.NotEqual(first.ConnectionId, Assert.IsType<DeviceConnected>(e).ConnectionId));
    }

    [Fact]
    public void Repartition_IsTreatedAsMediaChange()
    {
        _inventory.Reconcile([TestDevices.Disk(TestDevices.Device(partitionTableId: "mbr:C3072E18"))], T0);

        var events = _inventory.Reconcile([TestDevices.Disk(TestDevices.Device(partitionTableId: "gpt:0000"))], T0.AddSeconds(1));

        Assert.Equal(DisconnectReason.MediaChanged, Assert.IsType<DeviceDisconnected>(events[0]).Reason);
    }

    [Fact]
    public void DuplicateDiskInSnapshot_IsTrackedOnce()
    {
        var events = _inventory.Reconcile([TestDevices.Disk(), TestDevices.Disk()], T0);

        Assert.Single(events);
        Assert.Single(_inventory.Connections);
    }
}
