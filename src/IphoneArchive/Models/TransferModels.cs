namespace IphoneArchive.Models;

public sealed record PortableDeviceInfo(string Id, string Name, string Manufacturer)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Manufacturer) || Name.Contains(Manufacturer, StringComparison.OrdinalIgnoreCase)
        ? Name
        : $"{Name} — {Manufacturer}";
}

public sealed class PortableMediaItem
{
    public required string ObjectId { get; init; }
    public required string PersistentId { get; init; }
    public required string OriginalFileName { get; init; }
    public required ulong Size { get; init; }
    public required DateTimeOffset CaptureTime { get; set; }

    public string Extension => Path.GetExtension(OriginalFileName);
    public string BaseName => Path.GetFileNameWithoutExtension(OriginalFileName);
    public string SelectionKey => !string.IsNullOrWhiteSpace(PersistentId)
        ? PersistentId
        : $"{OriginalFileName}|{Size}|{CaptureTime.UtcTicks}";
}

public enum HeicExportMode
{
    OriginalsOnly = 0,
    HeicAndJpeg = 1,
    JpegOnly = 2
}

public sealed record ExportOptions(
    bool OrganizeByMonth,
    bool PrefixCaptureTime,
    bool VerifyCopies,
    HeicExportMode HeicMode,
    uint JpegQuality = 100);

public sealed class PhotoLibraryItem : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isSelected = true;

    public PhotoLibraryItem(PortableMediaItem item)
    {
        Source = item;
        SelectionKey = item.SelectionKey;
        OriginalFileName = item.OriginalFileName;
        Type = item.Extension.TrimStart('.').ToUpperInvariant();
        CaptureTime = item.CaptureTime.LocalDateTime;
        Size = FormatBytes(item.Size);
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public PortableMediaItem Source { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
    public string SelectionKey { get; }
    public string OriginalFileName { get; }
    public string Type { get; }
    public DateTime CaptureTime { get; }
    public string Size { get; }
    public ulong SizeBytes => Source.Size;

    private static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var display = (double)bytes;
        var unit = 0;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }
        return $"{display:0.##} {units[unit]}";
    }
}

public sealed record TransferProgress(
    string Stage,
    string CurrentFile,
    int ProcessedFiles,
    int TotalFiles,
    long ProcessedBytes,
    long TotalBytes,
    int Copied,
    int Skipped,
    int Errors,
    int JpegCreated = 0)
{
    public double Percentage => TotalBytes > 0
        ? Math.Clamp(ProcessedBytes * 100d / TotalBytes, 0, 100)
        : TotalFiles > 0 ? Math.Clamp(ProcessedFiles * 100d / TotalFiles, 0, 100) : 0;
}

public sealed record ExportSummary(int Found, int Copied, int Skipped, int Errors, int JpegCreated, long CopiedBytes, TimeSpan Elapsed);

