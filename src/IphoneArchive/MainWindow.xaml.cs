using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using IphoneArchive.Models;
using IphoneArchive.Services;
using Microsoft.Win32;

namespace IphoneArchive;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<PortableDeviceInfo> _devices = [];
    private List<PhotoLibraryItem> _libraryItems = [];
    private IReadOnlyList<PortableMediaItem>? _scannedMedia;
    private ICollectionView? _libraryView;
    private CancellationTokenSource? _transferCancellation;
    private string? _libraryDeviceId;
    private bool _busy;
    private bool _suppressSelectionMetrics;

    public MainWindow()
    {
        InitializeComponent();
        DeviceCombo.ItemsSource = _devices;

        var settings = AppSettings.Load();
        DestinationBox.Text = settings.Destination;
        OrganizeCheck.IsChecked = settings.OrganizeByMonth;
        PrefixCheck.IsChecked = settings.PrefixCaptureTime;
        VerifyCheck.IsChecked = settings.VerifyCopies;
        HeicModeCombo.SelectedIndex = (int)settings.HeicMode;

        ShowPage(0);
        UpdateMemoryStatus();
        Loaded += async (_, _) => await RefreshDevicesAsync();
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private async Task RefreshDevicesAsync()
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, allowCancel: false);
        FooterStatus.Text = "Looking for portable devices…";
        try
        {
            var devices = await Task.Run(WpdClient.ListDevices);
            _devices.Clear();
            foreach (var device in devices)
            {
                _devices.Add(device);
            }

            DeviceCombo.SelectedIndex = devices.Count > 0 ? 0 : -1;
            FooterStatus.Text = devices.Count == 0
                ? "No portable device found. Unlock the iPhone, reconnect the cable, and tap Trust."
                : $"Found {devices.Count:N0} portable device{(devices.Count == 1 ? string.Empty : "s")}.";
            HealthText.Text = devices.Count == 0
                ? "Windows did not report a connected portable device. Check the cable, unlock the phone and confirm Trust."
                : $"Windows WPD is responding. {devices.Count:N0} portable device{(devices.Count == 1 ? " is" : "s are")} available.";
        }
        catch (Exception exception)
        {
            FooterStatus.Text = "Windows could not enumerate portable devices.";
            HealthText.Text = "Device enumeration failed. Open the diagnostic folder after dismissing the error.";
            MessageBox.Show(this, FriendlyError(exception), "Device scan failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false, allowCancel: false);
            UpdateMemoryStatus();
        }
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var connected = DeviceCombo.SelectedItem is PortableDeviceInfo;
        ConnectionDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(connected ? "#10B981" : "#D97706"));
        ConnectionText.Text = connected ? "Device ready" : "Waiting for device";
        DeviceHint.Text = connected
            ? "Read-only connection. Scan once, then export without a second scan."
            : "Connect, unlock and trust this PC.";

        if (DeviceCombo.SelectedItem is not PortableDeviceInfo selected || selected.Id != _libraryDeviceId)
        {
            ClearLibrary();
        }
    }

    private void Navigation_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out var page))
        {
            ShowPage(page);
        }
    }

    private void ShowPage(int page)
    {
        if (PageLibrary is null || PageExport is null || PageDevice is null || PageAbout is null)
        {
            return;
        }

        PageLibrary.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageExport.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageDevice.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = page == 3 ? Visibility.Visible : Visibility.Collapsed;

        (WorkspaceTitle.Text, WorkspaceSubtitle.Text) = page switch
        {
            1 => ("Export & backup", "Create a verified, resumable Windows archive."),
            2 => ("Device center", "Connection health, capability status and diagnostics."),
            3 => ("About & safety", "Version, safeguards and supported behavior."),
            _ => ("Photo library", "Browse accessible media and choose exactly what to export.")
        };
    }

    private void GoToExport_Click(object sender, RoutedEventArgs e)
    {
        ExportNav.IsChecked = true;
    }

    private void HeicModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FormatHint is null)
        {
            return;
        }

        FormatHint.Text = HeicModeCombo.SelectedIndex switch
        {
            0 => "Preserves the original HEIC/HEIF file without creating a JPEG.",
            1 => "Keeps the original and also creates a maximum-quality JPEG copy.",
            _ => "Downloads HEIC only as a temporary source, writes a quality-100 JPEG, then removes the HEIC temporary file."
        };
    }

    private async void ScanLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (DeviceCombo.SelectedItem is not PortableDeviceInfo device)
        {
            MessageBox.Show(this, "Connect and select an iPhone first.", "No device selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _transferCancellation = new CancellationTokenSource();
        SetBusy(true, allowCancel: true);
        TransferTitle.Text = "Scanning photo library…";
        FooterStatus.Text = "Reading media metadata from the iPhone.";
        var reporter = new Progress<TransferProgress>(UpdateProgress);

        try
        {
            var media = await Task.Run(() =>
            {
                using var client = WpdClient.Open(device.Id);
                return client.Scan(reporter, _transferCancellation.Token);
            }, _transferCancellation.Token);

            SetLibrary(media, device.Id);
            TransferTitle.Text = "Library ready";
            CurrentFileText.Text = $"{media.Count:N0} items available";
            FooterStatus.Text = "Choose items in the library, then open Export & backup.";
        }
        catch (OperationCanceledException)
        {
            FooterStatus.Text = "Library scan cancelled.";
        }
        catch (Exception exception)
        {
            FooterStatus.Text = "The photo library could not be scanned.";
            MessageBox.Show(this, FriendlyError(exception), "Library scan failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _transferCancellation.Dispose();
            _transferCancellation = null;
            SetBusy(false, allowCancel: false);
            UpdateMemoryStatus();
        }
    }

    private void SetLibrary(IReadOnlyList<PortableMediaItem> media, string deviceId)
    {
        ClearLibrary();
        _scannedMedia = media;
        _libraryDeviceId = deviceId;
        _libraryItems = media.Select(item => new PhotoLibraryItem(item)).ToList();
        foreach (var item in _libraryItems)
        {
            item.PropertyChanged += LibraryItem_PropertyChanged;
        }

        _libraryView = CollectionViewSource.GetDefaultView(_libraryItems);
        _libraryView.Filter = FilterLibraryItem;
        LibraryGrid.ItemsSource = _libraryView;
        LibrarySummaryText.Text = media.Count == 0
            ? "No locally available photos or videos were exposed by the device."
            : $"{media.Count:N0} locally accessible media items — all selected for export.";
        UpdateLibraryMetrics();
    }

    private void ClearLibrary()
    {
        foreach (var item in _libraryItems)
        {
            item.PropertyChanged -= LibraryItem_PropertyChanged;
        }

        _libraryItems = [];
        _scannedMedia = null;
        _libraryView = null;
        _libraryDeviceId = null;
        if (LibraryGrid is not null)
        {
            LibraryGrid.ItemsSource = null;
        }
        if (LibrarySummaryText is not null)
        {
            LibrarySummaryText.Text = "Scan the connected iPhone to load its accessible photo library.";
            UpdateLibraryMetrics();
        }
    }

    private void LibraryItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_suppressSelectionMetrics && e.PropertyName == nameof(PhotoLibraryItem.IsSelected))
        {
            UpdateLibraryMetrics();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _libraryView?.Refresh();

    private void TypeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => _libraryView?.Refresh();

    private bool FilterLibraryItem(object value)
    {
        if (value is not PhotoLibraryItem item)
        {
            return false;
        }

        var search = SearchBox?.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(search) && !item.OriginalFileName.Contains(search, StringComparison.CurrentCultureIgnoreCase))
        {
            return false;
        }

        return TypeFilter?.SelectedIndex switch
        {
            1 => item.Type is "HEIC" or "HEIF" or "JPG" or "JPEG" or "PNG" or "GIF" or "TIF" or "TIFF",
            2 => item.Type is "MOV" or "MP4" or "M4V" or "AVI",
            3 => item.Type == "DNG",
            _ => true
        };
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetSelection(true);

    private void SelectNone_Click(object sender, RoutedEventArgs e) => SetSelection(false);

    private void SetSelection(bool selected)
    {
        _suppressSelectionMetrics = true;
        try
        {
            foreach (var item in _libraryItems)
            {
                item.IsSelected = selected;
            }
        }
        finally
        {
            _suppressSelectionMetrics = false;
        }

        UpdateLibraryMetrics();
        LibrarySummaryText.Text = selected
            ? $"All {_libraryItems.Count:N0} items selected."
            : "No library items selected.";
    }

    private void UpdateLibraryMetrics()
    {
        if (ItemCountText is null)
        {
            return;
        }

        var selectedCount = 0;
        ulong totalSize = 0;
        foreach (var item in _libraryItems)
        {
            totalSize += item.SizeBytes;
            if (item.IsSelected)
            {
                selectedCount++;
            }
        }

        ItemCountText.Text = _libraryItems.Count == 0 ? "—" : _libraryItems.Count.ToString("N0");
        LibrarySizeText.Text = _libraryItems.Count == 0 ? "—" : FormatBytes(totalSize);
        SelectedCountText.Text = _libraryItems.Count == 0 ? "—" : selectedCount.ToString("N0");
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the iPhone archive folder",
            Multiselect = false,
            InitialDirectory = Directory.Exists(DestinationBox.Text) ? DestinationBox.Text : null
        };

        if (dialog.ShowDialog(this) == true)
        {
            DestinationBox.Text = dialog.FolderName;
        }
    }

    private async void StartExport_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (DeviceCombo.SelectedItem is not PortableDeviceInfo device)
        {
            MessageBox.Show(this, "Connect and select an iPhone first.", "No device selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var destination = DestinationBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(destination))
        {
            MessageBox.Show(this, "Choose an export folder first.", "No destination", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, FriendlyError(exception), "Destination unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var options = new ExportOptions(
            OrganizeCheck.IsChecked == true,
            PrefixCheck.IsChecked == true,
            VerifyCheck.IsChecked == true,
            (HeicExportMode)Math.Clamp(HeicModeCombo.SelectedIndex, 0, 2),
            100);
        new AppSettings(destination, options.OrganizeByMonth, options.PrefixCaptureTime, options.VerifyCopies, options.HeicMode).Save();

        IReadOnlySet<string>? selectedKeys = null;
        IReadOnlyList<PortableMediaItem>? knownItems = null;
        if (_libraryDeviceId == device.Id && _libraryItems.Count > 0 && _scannedMedia is not null)
        {
            selectedKeys = _libraryItems.Where(item => item.IsSelected).Select(item => item.SelectionKey).ToHashSet(StringComparer.Ordinal);
            knownItems = _scannedMedia;
            if (selectedKeys.Count == 0)
            {
                MessageBox.Show(this, "Select at least one item in the photo library.", "Nothing selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        _transferCancellation = new CancellationTokenSource();
        SetBusy(true, allowCancel: true);
        TransferProgress.Value = 0;
        TransferTitle.Text = knownItems is null ? "Scanning iPhone…" : "Preparing selected items…";
        FooterStatus.Text = "Keep the iPhone unlocked and connected.";
        var reporter = new Progress<TransferProgress>(UpdateProgress);

        try
        {
            var engine = new ExportEngine();
            var summary = await Task.Run(
                () => engine.Export(device, destination, options, reporter, _transferCancellation.Token, selectedKeys, knownItems),
                _transferCancellation.Token);

            TransferProgress.Value = 100;
            PercentText.Text = "100%";
            TransferTitle.Text = summary.Errors == 0 ? "Export complete" : "Export finished with errors";
            CurrentFileText.Text = $"{summary.Found:N0} media items processed in {FormatDuration(summary.Elapsed)}";
            StatsText.Text = $"{summary.Copied:N0} copied  •  {summary.JpegCreated:N0} JPEGs  •  {summary.Skipped:N0} skipped  •  {summary.Errors:N0} errors";
            FooterStatus.Text = summary.Errors == 0
                ? $"Archive is up to date. Added {FormatBytes(summary.CopiedBytes)}."
                : "Some files could not be copied. Reconnect, scan again and resume to the same folder.";

            MessageBox.Show(
                this,
                $"Copied: {summary.Copied:N0}\nJPEG files created: {summary.JpegCreated:N0}\nAlready complete: {summary.Skipped:N0}\nErrors: {summary.Errors:N0}\nTransferred source data: {FormatBytes(summary.CopiedBytes)}",
                summary.Errors == 0 ? "Export complete" : "Export finished",
                MessageBoxButton.OK,
                summary.Errors == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            TransferTitle.Text = "Export cancelled";
            CurrentFileText.Text = "Completed files are recorded and will be skipped next time.";
            FooterStatus.Text = "Safe to reconnect and resume with the same destination.";
        }
        catch (Exception exception)
        {
            TransferTitle.Text = "Export stopped";
            FooterStatus.Text = "Reconnect the iPhone, scan again and resume. Completed files are safe.";
            MessageBox.Show(this, FriendlyError(exception), "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _transferCancellation.Dispose();
            _transferCancellation = null;
            SetBusy(false, allowCancel: false);
            UpdateMemoryStatus();
        }
    }

    private void UpdateProgress(TransferProgress progress)
    {
        TransferTitle.Text = progress.Stage;
        CurrentFileText.Text = progress.CurrentFile;
        TransferProgress.Value = progress.Percentage;
        PercentText.Text = $"{progress.Percentage:0}%";
        StatsText.Text = $"{progress.Copied:N0} copied  •  {progress.JpegCreated:N0} JPEGs  •  {progress.Skipped:N0} skipped  •  {progress.Errors:N0} errors";
        FooterStatus.Text = progress.TotalFiles > 0
            ? $"Item {Math.Min(progress.ProcessedFiles + 1, progress.TotalFiles):N0} of {progress.TotalFiles:N0}  •  {FormatBytes(progress.ProcessedBytes)} of {FormatBytes(progress.TotalBytes)}"
            : $"Scanning… {progress.ProcessedFiles:N0} media items found";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        TransferTitle.Text = "Cancelling safely…";
        _transferCancellation?.Cancel();
    }

    private void SetBusy(bool busy, bool allowCancel)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;
        StartButton.IsEnabled = !busy;
        DeviceCombo.IsEnabled = !busy;
        DestinationBox.IsEnabled = !busy;
        OrganizeCheck.IsEnabled = !busy;
        PrefixCheck.IsEnabled = !busy;
        VerifyCheck.IsEnabled = !busy;
        HeicModeCombo.IsEnabled = !busy;
        ScanLibraryButton.IsEnabled = !busy;
        SelectAllButton.IsEnabled = !busy && _libraryItems.Count > 0;
        SelectNoneButton.IsEnabled = !busy && _libraryItems.Count > 0;
        CancelButton.Visibility = allowCancel ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = allowCancel;
    }

    private void OpenDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IphoneArchive");
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, FriendlyError(exception), "Could not open diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateMemoryStatus()
    {
        try
        {
            var managed = GC.GetTotalMemory(forceFullCollection: false);
            using var process = Process.GetCurrentProcess();
            var working = process.WorkingSet64;
            FooterMemory.Text = $"Memory: {FormatBytes(managed)} managed";
            MemoryText.Text = $"Pooled USB buffers, virtualized rows and throttled progress are active. Current use: {FormatBytes(managed)} managed / {FormatBytes(working)} working set.";
        }
        catch
        {
            FooterMemory.Text = string.Empty;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy && _transferCancellation is not null)
        {
            var answer = MessageBox.Show(
                this,
                "Cancel the current operation and close? Completed files are safe and resumable.",
                "Operation in progress",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _transferCancellation.Cancel();
        }

        base.OnClosing(e);
    }

    private static string FriendlyError(Exception exception)
    {
        if (exception is UnauthorizedAccessException)
        {
            return "Windows denied access. Unlock the iPhone, confirm Trust, and make sure the destination folder is writable.";
        }

        if (exception.GetBaseException() is COMException comException &&
            (comException.HResult == unchecked((int)0x8007001E) || comException.HResult == unchecked((int)0x80042007)))
        {
            return "The iPhone stopped the current USB stream. Reconnect it, keep it unlocked, scan the library again, and resume to the same destination.";
        }

        var message = exception.GetBaseException().Message;
        return string.IsNullOrWhiteSpace(message) ? "An unexpected Windows error occurred." : message;
    }

    private static string FormatBytes(ulong bytes) => FormatBytes(checked((long)Math.Min(bytes, long.MaxValue)));

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var display = (double)Math.Max(0, bytes);
        var unit = 0;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }
        return $"{display:0.##} {units[unit]}";
    }

    private static string FormatDuration(TimeSpan duration)
        => duration.TotalHours >= 1 ? duration.ToString(@"h\h\ m\m") : duration.ToString(@"m\m\ s\s");
}
