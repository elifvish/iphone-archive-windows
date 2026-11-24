using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace IphoneArchive.Services;

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;

    public PropertyKey(string formatId, uint propertyId)
    {
        FormatId = Guid.Parse(formatId);
        PropertyId = propertyId;
    }
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort VariantType;
    [FieldOffset(8)] public long LongValue;
    [FieldOffset(8)] public double DoubleValue;
    [FieldOffset(8)] public IntPtr PointerValue;
}

internal static class WpdKeys
{
    private const string ObjectFormat = "EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C";
    private const string ClientFormat = "204D9F0C-2292-4080-9F42-40664E70F859";

    public static PropertyKey ObjectName = new(ObjectFormat, 4);
    public static PropertyKey ObjectPersistentUniqueId = new(ObjectFormat, 5);
    public static PropertyKey ObjectSize = new(ObjectFormat, 11);
    public static PropertyKey ObjectOriginalFileName = new(ObjectFormat, 12);
    public static PropertyKey ObjectDateCreated = new(ObjectFormat, 18);
    public static PropertyKey ObjectDateModified = new(ObjectFormat, 19);
    public static PropertyKey ObjectDateAuthored = new(ObjectFormat, 20);
    public static PropertyKey ResourceDefault = new("E81E79BE-34F0-41BF-B53F-F1A06AE87842", 0);

    public static PropertyKey ClientName = new(ClientFormat, 2);
    public static PropertyKey ClientMajorVersion = new(ClientFormat, 3);
    public static PropertyKey ClientMinorVersion = new(ClientFormat, 4);
    public static PropertyKey ClientRevision = new(ClientFormat, 5);
    public static PropertyKey ClientDesiredAccess = new(ClientFormat, 9);
}

internal static class WpdCom
{
    public static readonly Guid PortableDeviceManagerClass = Guid.Parse("0AF10CEC-2ECD-4B92-9581-34F6AE0637F3");
    public static readonly Guid PortableDeviceClass = Guid.Parse("728A21C5-3D9E-48D7-9810-864848F0F404");
    public static readonly Guid PortableDeviceFtmClass = Guid.Parse("F7C0039A-4762-488A-B4B2-760EF9A1BA9B");
    public static readonly Guid PortableDeviceValuesClass = Guid.Parse("0C15D503-D017-47CE-9016-7B3F978721CC");
    public static readonly Guid PortableDeviceKeyCollectionClass = Guid.Parse("DE2D022D-2480-43BE-97F0-D1FA2CF98F4F");

    public const uint GenericRead = 0x80000000;
    public const uint StgmRead = 0;
    public const string DeviceObjectId = "DEVICE";

    public static T Create<T>(Guid classId) where T : class
    {
        var type = Type.GetTypeFromCLSID(classId, throwOnError: true)!;
        return (T)Activator.CreateInstance(type)!;
    }

    public static void ThrowIfFailed(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PropVariant propVariant);
}

[ComImport, Guid("A1567595-4C2F-4574-A6FA-ECEF917B9A40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceManager
{
    [PreserveSig] int GetDevices(
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[]? deviceIds,
        ref uint count);
    [PreserveSig] int RefreshDeviceList();
    [PreserveSig] int GetDeviceFriendlyName([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder? name, ref uint nameLength);
    [PreserveSig] int GetDeviceDescription([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder? description, ref uint descriptionLength);
    [PreserveSig] int GetDeviceManufacturer([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder? manufacturer, ref uint manufacturerLength);
}

[ComImport, Guid("625E2DF8-6392-4CF0-9AD1-3CFA5F17775C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDevice
{
    [PreserveSig] int Open([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IPortableDeviceValues clientInfo);
    [PreserveSig] int SendCommand(uint flags, IPortableDeviceValues parameters, out IPortableDeviceValues results);
    [PreserveSig] int Content(out IPortableDeviceContent content);
    [PreserveSig] int Capabilities([MarshalAs(UnmanagedType.Interface)] out object capabilities);
    [PreserveSig] int Cancel();
    [PreserveSig] int Close();
    [PreserveSig] int Advise(uint flags, [MarshalAs(UnmanagedType.Interface)] object callback, IPortableDeviceValues parameters, [MarshalAs(UnmanagedType.LPWStr)] out string cookie);
    [PreserveSig] int Unadvise([MarshalAs(UnmanagedType.LPWStr)] string cookie);
    [PreserveSig] int GetPnPDeviceID([MarshalAs(UnmanagedType.LPWStr)] out string deviceId);
}

[ComImport, Guid("6A96ED84-7C73-4480-9938-BF5AF477D426"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceContent
{
    [PreserveSig] int EnumObjects(uint flags, [MarshalAs(UnmanagedType.LPWStr)] string parentObjectId, IPortableDeviceValues? filter, out IEnumPortableDeviceObjectIds objectIds);
    [PreserveSig] int Properties(out IPortableDeviceProperties properties);
    [PreserveSig] int Transfer(out IPortableDeviceResources resources);
    [PreserveSig] int CreateObjectWithPropertiesOnly(IPortableDeviceValues values, [MarshalAs(UnmanagedType.LPWStr)] out string objectId);
    [PreserveSig] int CreateObjectWithPropertiesAndData(IPortableDeviceValues values, out IStream stream, ref uint optimalWriteBufferSize, [MarshalAs(UnmanagedType.LPWStr)] out string cookie);
    [PreserveSig] int Delete(uint options, [MarshalAs(UnmanagedType.Interface)] object objectIds, out IPortableDevicePropVariantCollection results);
    [PreserveSig] int GetObjectIDsFromPersistentUniqueIDs(IPortableDevicePropVariantCollection persistentIds, out IPortableDevicePropVariantCollection objectIds);
    [PreserveSig] int Cancel();
    [PreserveSig] int Move(IPortableDevicePropVariantCollection objectIds, [MarshalAs(UnmanagedType.LPWStr)] string destinationFolderObjectId, out IPortableDevicePropVariantCollection results);
    [PreserveSig] int Copy(IPortableDevicePropVariantCollection objectIds, [MarshalAs(UnmanagedType.LPWStr)] string destinationFolderObjectId, out IPortableDevicePropVariantCollection results);
}

[ComImport, Guid("10ECE955-CF41-4728-BFA0-41EEDF1BBF19"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumPortableDeviceObjectIds
{
    [PreserveSig] int Next(uint requested, [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 0)] string[] objectIds, ref uint fetched);
    [PreserveSig] int Skip(uint count);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(out IEnumPortableDeviceObjectIds clone);
    [PreserveSig] int Cancel();
}

[ComImport, Guid("7F6D695C-03DF-4439-A809-59266BEEE3A6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceProperties
{
    [PreserveSig] int GetSupportedProperties([MarshalAs(UnmanagedType.LPWStr)] string objectId, out IPortableDeviceKeyCollection keys);
    [PreserveSig] int GetPropertyAttributes([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key, out IPortableDeviceValues attributes);
    [PreserveSig] int GetValues([MarshalAs(UnmanagedType.LPWStr)] string objectId, IPortableDeviceKeyCollection? keys, out IPortableDeviceValues values);
    [PreserveSig] int SetValues([MarshalAs(UnmanagedType.LPWStr)] string objectId, IPortableDeviceValues values, out IPortableDeviceValues results);
    [PreserveSig] int Delete([MarshalAs(UnmanagedType.LPWStr)] string objectId, IPortableDeviceKeyCollection keys);
    [PreserveSig] int Cancel();
}

[ComImport, Guid("FD8878AC-D841-4D17-891C-E6829CDB6934"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceResources
{
    [PreserveSig] int GetSupportedResources([MarshalAs(UnmanagedType.LPWStr)] string objectId, out IPortableDeviceKeyCollection keys);
    [PreserveSig] int GetResourceAttributes([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key, out IPortableDeviceValues attributes);
    [PreserveSig] int GetStream([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key, uint mode, ref uint optimalBufferSize, out IStream stream);
    [PreserveSig] int Delete([MarshalAs(UnmanagedType.LPWStr)] string objectId, IPortableDeviceKeyCollection keys);
    [PreserveSig] int Cancel();
    [PreserveSig] int CreateResource(IPortableDeviceValues attributes, out IStream stream, ref uint optimalWriteBufferSize, [MarshalAs(UnmanagedType.LPWStr)] out string cookie);
}

[ComImport, Guid("DADA2357-E0AD-492E-98DB-DD61C53BA353"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceKeyCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int Add(ref PropertyKey key);
    [PreserveSig] int Clear();
    [PreserveSig] int RemoveAt(uint index);
}

[ComImport, Guid("6848F6F2-3155-4F86-B6F5-263EEEAB3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceValues
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int GetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] out string value);
    [PreserveSig] int SetUnsignedIntegerValue(ref PropertyKey key, uint value);
    [PreserveSig] int GetUnsignedIntegerValue(ref PropertyKey key, out uint value);
    [PreserveSig] int SetSignedIntegerValue(ref PropertyKey key, int value);
    [PreserveSig] int GetSignedIntegerValue(ref PropertyKey key, out int value);
    [PreserveSig] int SetUnsignedLargeIntegerValue(ref PropertyKey key, ulong value);
    [PreserveSig] int GetUnsignedLargeIntegerValue(ref PropertyKey key, out ulong value);
    [PreserveSig] int SetSignedLargeIntegerValue(ref PropertyKey key, long value);
    [PreserveSig] int GetSignedLargeIntegerValue(ref PropertyKey key, out long value);
}

[ComImport, Guid("89B2E422-4F1B-4316-BCEF-A44AFEA83EB3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDevicePropVariantCollection
{
}

