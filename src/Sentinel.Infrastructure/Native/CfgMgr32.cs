using System.Runtime.InteropServices;
using System.Text;

namespace Sentinel.Infrastructure.Native;

/// <summary>Read-only Configuration Manager (cfgmgr32.dll) device-tree queries.</summary>
internal static partial class CfgMgr32
{
    private const uint CrSuccess = 0x00;
    private const uint CrBufferSmall = 0x1A;
    private const uint CmLocateDevNodeNormal = 0x00;
    private const uint DevPropTypeGuid = 0x0D;
    private const uint DevPropTypeString = 0x12;

    /// <summary>DEVPKEY_Device_Parent.</summary>
    public static readonly DevPropKey DeviceParent = new(new Guid("4340a6c5-93fa-4706-972c-7b648008a5a7"), 8);

    /// <summary>DEVPKEY_Device_ContainerId: groups all device nodes of one physical device.</summary>
    public static readonly DevPropKey DeviceContainerId = new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct DevPropKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    /// <summary>Returns a string property of a present device node, or null if unavailable.</summary>
    public static string? GetStringProperty(string instanceId, in DevPropKey key)
    {
        var buffer = GetProperty(instanceId, key, DevPropTypeString);
        return buffer is null ? null : Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    /// <summary>Returns a GUID property of a present device node, or null if unavailable.</summary>
    public static Guid? GetGuidProperty(string instanceId, in DevPropKey key)
    {
        var buffer = GetProperty(instanceId, key, DevPropTypeGuid);
        return buffer is { Length: >= 16 } ? new Guid(buffer.AsSpan(0, 16)) : null;
    }

    private static unsafe byte[]? GetProperty(string instanceId, in DevPropKey key, uint expectedType)
    {
        if (CM_Locate_DevNodeW(out var devInst, instanceId, CmLocateDevNodeNormal) != CrSuccess)
        {
            return null;
        }

        uint size = 0;
        var result = CM_Get_DevNode_PropertyW(devInst, key, out _, null, ref size, 0);
        if (result != CrBufferSmall || size == 0)
        {
            return null;
        }

        var buffer = new byte[size];
        uint type;
        fixed (byte* pointer = buffer)
        {
            result = CM_Get_DevNode_PropertyW(devInst, key, out type, pointer, ref size, 0);
        }

        return result == CrSuccess && type == expectedType ? buffer : null;
    }

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static unsafe partial uint CM_Get_DevNode_PropertyW(
        uint devInst,
        in DevPropKey propertyKey,
        out uint propertyType,
        byte* propertyBuffer,
        ref uint propertyBufferSize,
        uint flags);
}
