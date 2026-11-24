using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Diagnostics;
using IphoneArchive.Models;

namespace IphoneArchive.Services;

public sealed class WpdClient : IDisposable
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".heic", ".heif", ".jpg", ".jpeg", ".png", ".gif", ".tif", ".tiff",
        ".dng", ".mov", ".mp4", ".m4v", ".avi"
    };

    private readonly IPortableDevice _device;
    private readonly IPortableDeviceContent _content;
    private readonly IPortableDeviceProperties _properties;
    private readonly IPortableDeviceResources _resources;
    private bool _disposed;

    private WpdClient(string deviceId)
    {
        // Microsoft recommends the free-threaded-marshaler variant for managed
        // applications because WPD calls can cross apartment boundaries.
        _device = WpdCom.Create<IPortableDevice>(WpdCom.PortableDeviceFtmClass);
        var clientInfo = WpdCom.Create<IPortableDeviceValues>(WpdCom.PortableDeviceValuesClass);

        try
        {
            WpdCom.ThrowIfFailed(clientInfo.SetStringValue(ref WpdKeys.ClientName, "iPhone Archive"));
            WpdCom.ThrowIfFailed(clientInfo.SetUnsignedIntegerValue(ref WpdKeys.ClientMajorVersion, 1));
            WpdCom.ThrowIfFailed(clientInfo.SetUnsignedIntegerValue(ref WpdKeys.ClientMinorVersion, 0));
            WpdCom.ThrowIfFailed(clientInfo.SetUnsignedIntegerValue(ref WpdKeys.ClientRevision, 0));
            WpdCom.ThrowIfFailed(clientInfo.SetUnsignedIntegerValue(ref WpdKeys.ClientDesiredAccess, WpdCom.GenericRead));
            WpdCom.ThrowIfFailed(_device.Open(deviceId, clientInfo));
        }
        finally
        {
            Release(clientInfo);
        }

        try
        {
            WpdCom.ThrowIfFailed(_device.Content(out _content));
            WpdCom.ThrowIfFailed(_content.Properties(out _properties));
            WpdCom.ThrowIfFailed(_content.Transfer(out _resources));
        }
        catch
        {
            try
            {
                _device.Close();
            }
            catch
            {
                // Preserve the connection error that prevented initialization.
            }
            Release(_device);
            throw;
        }
    }

    public static WpdClient Open(string deviceId) => new(deviceId);

    public static IReadOnlyList<PortableDeviceInfo> ListDevices()
    {
        var manager = WpdCom.Create<IPortableDeviceManager>(WpdCom.PortableDeviceManagerClass);
        try
        {
            manager.RefreshDeviceList();
            uint count = 0;
            WpdCom.ThrowIfFailed(manager.GetDevices(null, ref count));
            if (count == 0)
            {
                return Array.Empty<PortableDeviceInfo>();
            }

            var ids = new string[count];
            WpdCom.ThrowIfFailed(manager.GetDevices(ids, ref count));
            var devices = new List<PortableDeviceInfo>((int)count);

            foreach (var id in ids.Take((int)count).Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                var friendlyName = ReadManagerString((System.Text.StringBuilder? buffer, ref uint length) => manager.GetDeviceFriendlyName(id, buffer, ref length));
                var manufacturer = ReadManagerString((System.Text.StringBuilder? buffer, ref uint length) => manager.GetDeviceManufacturer(id, buffer, ref length));
                devices.Add(new PortableDeviceInfo(id, string.IsNullOrWhiteSpace(friendlyName) ? "Portable device" : friendlyName, manufacturer));
            }

            return devices
                .OrderByDescending(device => device.Name.Contains("iPhone", StringComparison.OrdinalIgnoreCase))
                .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        finally
        {
            Release(manager);
        }
    }

    public IReadOnlyList<PortableMediaItem> Scan(IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        var items = new List<PortableMediaItem>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        var progressClock = Stopwatch.StartNew();
        pending.Push(WpdCom.DeviceObjectId);

        var keys = CreateMediaPropertyKeys();
        try
        {
            while (pending.TryPop(out var parentId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!visited.Add(parentId))
                {
                    continue;
                }

                EnumerateChildren(parentId, pending, items, keys, progress, progressClock, cancellationToken);
            }

            AlignLivePhotoPairTimes(items);
            progress?.Report(new TransferProgress("Scanning", items.Count == 0 ? "No media found" : items[^1].OriginalFileName, items.Count, 0, 0, 0, 0, 0, 0));
            return items.OrderBy(item => item.CaptureTime).ThenBy(item => item.OriginalFileName, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally
        {
            Release(keys);
        }
    }

    public IStream OpenRead(PortableMediaItem item, out uint optimalBufferSize)
    {
        optimalBufferSize = 0;
        var key = WpdKeys.ResourceDefault;
        WpdCom.ThrowIfFailed(_resources.GetStream(item.ObjectId, ref key, WpdCom.StgmRead, ref optimalBufferSize, out var stream));
        return stream;
    }

    private void EnumerateChildren(
        string parentId,
        Stack<string> pending,
        List<PortableMediaItem> items,
        IPortableDeviceKeyCollection keys,
        IProgress<TransferProgress>? progress,
        Stopwatch progressClock,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IEnumPortableDeviceObjectIds? enumerator = null;
        try
        {
            var hr = _content.EnumObjects(0, parentId, null, out enumerator);
            if (hr < 0 || enumerator is null)
            {
                return;
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = new string[32];
                uint fetched = 0;
                hr = enumerator.Next((uint)batch.Length, batch, ref fetched);
                if (hr < 0 || fetched == 0)
                {
                    break;
                }

                foreach (var objectId in batch.Take((int)fetched))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(objectId))
                    {
                        continue;
                    }

                    var item = TryReadMediaItem(objectId, keys);
                    if (item is not null)
                    {
                        items.Add(item);
                        if (items.Count == 1 || items.Count % 50 == 0 || progressClock.ElapsedMilliseconds >= 200)
                        {
                            progress?.Report(new TransferProgress("Scanning", item.OriginalFileName, items.Count, 0, 0, 0, 0, 0, 0));
                            progressClock.Restart();
                        }
                    }

                    // WPD exposes a tree. Iteration avoids a stack overflow on malformed
                    // devices and keeps COM enumerators short-lived.
                    pending.Push(objectId);
                }

                if (fetched < batch.Length)
                {
                    break;
                }
            }
        }
        finally
        {
            if (enumerator is not null && Marshal.IsComObject(enumerator))
            {
                Release(enumerator);
            }
        }
    }

    private static IPortableDeviceKeyCollection CreateMediaPropertyKeys()
    {
        var keys = WpdCom.Create<IPortableDeviceKeyCollection>(WpdCom.PortableDeviceKeyCollectionClass);
        AddKey(keys, ref WpdKeys.ObjectName);
        AddKey(keys, ref WpdKeys.ObjectOriginalFileName);
        AddKey(keys, ref WpdKeys.ObjectPersistentUniqueId);
        AddKey(keys, ref WpdKeys.ObjectSize);
        AddKey(keys, ref WpdKeys.ObjectDateCreated);
        AddKey(keys, ref WpdKeys.ObjectDateAuthored);
        AddKey(keys, ref WpdKeys.ObjectDateModified);
        return keys;
    }

    private PortableMediaItem? TryReadMediaItem(string objectId, IPortableDeviceKeyCollection keys)
    {
        IPortableDeviceValues? values = null;
        try
        {
            var hr = _properties.GetValues(objectId, keys, out values);
            if (hr < 0 || values is null)
            {
                return null;
            }

            var fileName = ReadString(values, ref WpdKeys.ObjectOriginalFileName);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = ReadString(values, ref WpdKeys.ObjectName);
            }

            var extension = Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(fileName) || !SupportedExtensions.Contains(extension))
            {
                return null;
            }

            var size = ReadUnsignedLong(values, ref WpdKeys.ObjectSize);
            var persistentId = ReadString(values, ref WpdKeys.ObjectPersistentUniqueId);
            var captureTime = ReadDate(values, ref WpdKeys.ObjectDateAuthored)
                              ?? ReadDate(values, ref WpdKeys.ObjectDateCreated)
                              ?? ReadDate(values, ref WpdKeys.ObjectDateModified)
                              ?? DateTimeOffset.Now;

            return new PortableMediaItem
            {
                ObjectId = objectId,
                PersistentId = persistentId,
                OriginalFileName = Path.GetFileName(fileName),
                Size = size,
                CaptureTime = captureTime
            };
        }
        finally
        {
            if (values is not null && Marshal.IsComObject(values))
            {
                Release(values);
            }
        }
    }

    private static void AddKey(IPortableDeviceKeyCollection keys, ref PropertyKey key)
        => WpdCom.ThrowIfFailed(keys.Add(ref key));

    private static string ReadManagerString(ManagerStringReader reader)
    {
        uint length = 0;
        var hr = reader(null, ref length);
        if (hr < 0 || length == 0)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder((int)length);
        WpdCom.ThrowIfFailed(reader(builder, ref length));
        return builder.ToString();
    }

    private delegate int ManagerStringReader(System.Text.StringBuilder? buffer, ref uint length);

    private static string ReadString(IPortableDeviceValues values, ref PropertyKey key)
    {
        var hr = values.GetStringValue(ref key, out var value);
        return hr < 0 ? string.Empty : value ?? string.Empty;
    }

    private static ulong ReadUnsignedLong(IPortableDeviceValues values, ref PropertyKey key)
    {
        var hr = values.GetUnsignedLargeIntegerValue(ref key, out var value);
        return hr < 0 ? 0 : value;
    }

    private static DateTimeOffset? ReadDate(IPortableDeviceValues values, ref PropertyKey key)
    {
        var hr = values.GetValue(ref key, out var variant);
        if (hr < 0)
        {
            return null;
        }

        try
        {
            return variant.VariantType switch
            {
                7 when !double.IsNaN(variant.DoubleValue) => new DateTimeOffset(DateTime.SpecifyKind(DateTime.FromOADate(variant.DoubleValue), DateTimeKind.Local)),
                64 when variant.LongValue > 0 => new DateTimeOffset(DateTime.FromFileTimeUtc(variant.LongValue)),
                _ => null
            };
        }
        catch (ArgumentException)
        {
            return null;
        }
        finally
        {
            WpdCom.PropVariantClear(ref variant);
        }
    }

    private static void AlignLivePhotoPairTimes(List<PortableMediaItem> items)
    {
        foreach (var group in items.GroupBy(item => item.BaseName, StringComparer.OrdinalIgnoreCase))
        {
            var image = group.FirstOrDefault(item => item.Extension.Equals(".heic", StringComparison.OrdinalIgnoreCase)
                                                     || item.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                                                     || item.Extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase));
            var movie = group.FirstOrDefault(item => item.Extension.Equals(".mov", StringComparison.OrdinalIgnoreCase));
            if (image is null || movie is null)
            {
                continue;
            }

            movie.CaptureTime = image.CaptureTime;
            foreach (var sidecar in group.Where(item => item.Extension.Equals(".aae", StringComparison.OrdinalIgnoreCase)))
            {
                sidecar.CaptureTime = image.CaptureTime;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _device.Close();
        }
        catch
        {
            // Device may have been unplugged. Releasing COM references is still required.
        }

        Release(_resources);
        Release(_properties);
        Release(_content);
        Release(_device);
    }

    private static void Release(object comObject)
    {
        try
        {
            if (Marshal.IsComObject(comObject))
            {
                Marshal.ReleaseComObject(comObject);
            }
        }
        catch (InvalidComObjectException)
        {
            // The runtime can disconnect an RCW after a USB removal.
        }
        catch (COMException)
        {
            // Cleanup must never replace the original device error.
        }
    }
}

