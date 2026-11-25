namespace Sentinel.Core.Devices;

/// <summary>
/// Storage bus type. Numeric values match the Windows <c>STORAGE_BUS_TYPE</c> enumeration,
/// which is also what <c>MSFT_Disk.BusType</c> reports.
/// </summary>
public enum StorageBusType
{
    Unknown = 0,
    Scsi = 1,
    Atapi = 2,
    Ata = 3,
    Ieee1394 = 4,
    Ssa = 5,
    FibreChannel = 6,
    Usb = 7,
    Raid = 8,
    Iscsi = 9,
    Sas = 10,
    Sata = 11,
    Sd = 12,
    Mmc = 13,
    Virtual = 14,
    FileBackedVirtual = 15,
    Spaces = 16,
    Nvme = 17,
    Scm = 18,
    Ufs = 19,
}

public static class StorageBusClassification
{
    /// <summary>
    /// Buses that PC Sentinel treats as external, removable storage. Classification is by bus,
    /// never by <c>DriveType</c>: many USB SSDs report themselves as fixed disks.
    /// </summary>
    public static bool IsExternalStorageBus(StorageBusType busType) =>
        busType is StorageBusType.Usb or StorageBusType.Sd or StorageBusType.Mmc;

    public static StorageBusType FromRaw(int raw) =>
        Enum.IsDefined((StorageBusType)raw) ? (StorageBusType)raw : StorageBusType.Unknown;
}
