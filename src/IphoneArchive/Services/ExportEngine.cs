using System.Diagnostics;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using IphoneArchive.Models;
using ImageMagick;

namespace IphoneArchive.Services;

public sealed class ExportEngine
{
    public ExportSummary Export(
        PortableDeviceInfo device,
        string destinationRoot,
        ExportOptions options,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? selectedKeys = null,
        IReadOnlyList<PortableMediaItem>? knownItems = null)
    {
        var watch = Stopwatch.StartNew();
        using var session = new WpdSessionHolder(device.Id);
        using var manifest = new ManifestStore(destinationRoot);

        // Reuse a fresh library scan when the UI already has one. Besides being faster,
        // this halves the number of WPD calls made during a normal export.
        var scannedItems = knownItems ?? session.Current.Scan(progress, cancellationToken);
        var items = selectedKeys is null
            ? scannedItems
            : scannedItems.Where(item => selectedKeys.Contains(item.SelectionKey)).ToArray();
        var totalBytes = items.Aggregate<PortableMediaItem, long>(0, (sum, item) => checked(sum + (long)item.Size));
        var completedBytes = 0L;
        var copiedBytes = 0L;
        var copied = 0;
        var skipped = 0;
        var errors = 0;
        var jpegCreated = 0;
        var processed = 0;

        progress?.Report(new TransferProgress("Preparing", $"Found {items.Count:N0} media items", 0, items.Count, 0, totalBytes, 0, 0, 0, jpegCreated));

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceKey = BuildSourceKey(device.Id, item, options.HeicMode);

            if (manifest.IsCompleted(sourceKey, item.Size, out var completedRelativePath))
            {
                skipped++;
                processed++;
                completedBytes += checked((long)item.Size);
                if (options.HeicMode == HeicExportMode.HeicAndJpeg && IsHeic(item) && !string.IsNullOrWhiteSpace(completedRelativePath))
                {
                    var originalPath = Path.Combine(destinationRoot, completedRelativePath);
                    try
                    {
                        if (CreateJpegCopy(originalPath, item, options.JpegQuality, progress, processed, items.Count, completedBytes, totalBytes, copied, skipped, errors, jpegCreated, cancellationToken))
                        {
                            jpegCreated++;
                        }
                    }
                    catch (Exception exception)
                    {
                        errors++;
                        AppendErrorLog(destinationRoot, item, "JPEG conversion", exception);
                    }
                }
                progress?.Report(new TransferProgress("Skipping completed", item.OriginalFileName, processed, items.Count, completedBytes, totalBytes, copied, skipped, errors, jpegCreated));
                continue;
            }

            try
            {
                var targetPath = PlanTargetPath(destinationRoot, item, options);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                targetPath = MakeUniquePath(targetPath);

                if (options.HeicMode == HeicExportMode.JpegOnly && IsHeic(item))
                {
                    var heicTemporaryPath = targetPath + ".source-heic.partial";
                    try
                    {
                        CopyOneWithRetry(session, item, heicTemporaryPath, options.VerifyCopies, completedBytes, totalBytes, processed, items.Count, copied, skipped, errors, jpegCreated, progress, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        var jpegHash = CreateJpegOnly(heicTemporaryPath, targetPath, item, options.JpegQuality, progress, processed, items.Count, completedBytes + checked((long)item.Size), totalBytes, copied, skipped, errors, jpegCreated, cancellationToken);
                        manifest.MarkCompleted(item, sourceKey, targetPath, jpegHash);
                        jpegCreated++;
                    }
                    finally
                    {
                        TryDeletePartial(heicTemporaryPath);
                    }
                }
                else
                {
                    var temporaryPath = targetPath + ".partial";
                    var hash = CopyOneWithRetry(session, item, temporaryPath, options.VerifyCopies, completedBytes, totalBytes, processed, items.Count, copied, skipped, errors, jpegCreated, progress, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    File.Move(temporaryPath, targetPath, overwrite: false);
                    ApplyCaptureTimestamps(targetPath, item.CaptureTime);
                    manifest.MarkCompleted(item, sourceKey, targetPath, hash);

                    if (options.HeicMode == HeicExportMode.HeicAndJpeg && IsHeic(item))
                    {
                        try
                        {
                            if (CreateJpegCopy(targetPath, item, options.JpegQuality, progress, processed, items.Count, completedBytes + checked((long)item.Size), totalBytes, copied, skipped, errors, jpegCreated, cancellationToken))
                            {
                                jpegCreated++;
                            }
                        }
                        catch (Exception exception)
                        {
                            errors++;
                            AppendErrorLog(destinationRoot, item, "JPEG conversion", exception);
                        }
                    }
                }

                copied++;
                processed++;
                copiedBytes += checked((long)item.Size);
                completedBytes += checked((long)item.Size);
                progress?.Report(new TransferProgress("Copied", item.OriginalFileName, processed, items.Count, completedBytes, totalBytes, copied, skipped, errors, jpegCreated));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                errors++;
                processed++;
                completedBytes += checked((long)item.Size);
                AppendErrorLog(destinationRoot, item, "Original transfer", exception);
                progress?.Report(new TransferProgress("Error — continuing", item.OriginalFileName, processed, items.Count, completedBytes, totalBytes, copied, skipped, errors, jpegCreated));
            }
        }

        watch.Stop();
        return new ExportSummary(items.Count, copied, skipped, errors, jpegCreated, copiedBytes, watch.Elapsed);
    }

    private static string CopyOneWithRetry(
        WpdSessionHolder session,
        PortableMediaItem item,
        string temporaryPath,
        bool verify,
        long completedBytesBeforeFile,
        long totalBytes,
        int processedFiles,
        int totalFiles,
        int copied,
        int skipped,
        int errors,
        int jpegCreated,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 2;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return CopyOne(session.Current, item, temporaryPath, verify, completedBytesBeforeFile, totalBytes, processedFiles, totalFiles, copied, skipped, errors, jpegCreated, progress, cancellationToken);
            }
            catch (Exception exception) when (exception is COMException or DeviceStreamMismatchException)
            {
                lastException = exception;
                session.Reset();
                if (attempt == maximumAttempts)
                {
                    break;
                }

                progress?.Report(new TransferProgress($"Device read failed — retrying {attempt + 1}/{maximumAttempts}", item.OriginalFileName, processedFiles, totalFiles, completedBytesBeforeFile, totalBytes, copied, skipped, errors, jpegCreated));
            }
        }

        throw new IOException($"Could not read {item.OriginalFileName} after {maximumAttempts} attempts.", lastException);
    }

    private static string CopyOne(
        WpdClient client,
        PortableMediaItem item,
        string temporaryPath,
        bool verify,
        long completedBytesBeforeFile,
        long totalBytes,
        int processedFiles,
        int totalFiles,
        int copied,
        int skipped,
        int errors,
        int jpegCreated,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        var stream = client.OpenRead(item, out var optimalBufferSize);
        try
        {
            var bufferSize = (int)Math.Clamp(optimalBufferSize == 0 ? 256 * 1024u : optimalBufferSize, 64 * 1024u, 1024 * 1024u);
            var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            var bytesReadPointer = Marshal.AllocCoTaskMem(sizeof(int));
            long currentFileBytes = 0;
            var progressClock = Stopwatch.StartNew();

            try
            {
                string sourceHash;
                using (var sha = SHA256.Create())
                {
                    // The output stream must be disposed before verification reopens the
                    // temporary file. FileShare.None intentionally prevents concurrent reads.
                    using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, FileOptions.SequentialScan | FileOptions.WriteThrough))
                    {
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            Marshal.WriteInt32(bytesReadPointer, 0);
                            stream.Read(buffer, bufferSize, bytesReadPointer);
                            var bytesRead = Marshal.ReadInt32(bytesReadPointer);
                            if (bytesRead <= 0)
                            {
                                break;
                            }

                            output.Write(buffer, 0, bytesRead);
                            sha.TransformBlock(buffer, 0, bytesRead, null, 0);
                            currentFileBytes += bytesRead;
                            // A progress event for every USB chunk can queue thousands of
                            // dispatcher callbacks and make the UI appear to leak memory.
                            if (progressClock.ElapsedMilliseconds >= 100 || (item.Size > 0 && (ulong)currentFileBytes >= item.Size))
                            {
                                progress?.Report(new TransferProgress(
                                    "Copying",
                                    item.OriginalFileName,
                                    processedFiles,
                                    totalFiles,
                                    completedBytesBeforeFile + currentFileBytes,
                                    totalBytes,
                                    copied,
                                    skipped,
                                    errors,
                                    jpegCreated));
                                progressClock.Restart();
                            }
                        }

                        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        output.Flush(flushToDisk: true);
                    }

                    sourceHash = Convert.ToHexString(sha.Hash!);
                }

                if (item.Size > 0 && (ulong)currentFileBytes != item.Size)
                {
                    throw new DeviceStreamMismatchException($"Incomplete copy for {item.OriginalFileName}: expected {item.Size:N0} bytes, received {currentFileBytes:N0}.");
                }

                if (verify)
                {
                    progress?.Report(new TransferProgress("Verifying", item.OriginalFileName, processedFiles, totalFiles, completedBytesBeforeFile + currentFileBytes, totalBytes, copied, skipped, errors, jpegCreated));
                    using var verifyStream = File.OpenRead(temporaryPath);
                    var destinationHash = Convert.ToHexString(SHA256.HashData(verifyStream));
                    if (!destinationHash.Equals(sourceHash, StringComparison.Ordinal))
                    {
                        throw new IOException($"Verification failed for {item.OriginalFileName}.");
                    }
                }

                return sourceHash;
            }
            catch
            {
                TryDeletePartial(temporaryPath);
                throw;
            }
            finally
            {
                Marshal.FreeCoTaskMem(bytesReadPointer);
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            try
            {
                if (Marshal.IsComObject(stream))
                {
                    Marshal.ReleaseComObject(stream);
                }
            }
            catch (InvalidComObjectException)
            {
                // USB removal can disconnect the RCW before cleanup runs.
            }
            catch (COMException)
            {
                // Cleanup must not mask a more useful transfer error.
            }
        }
    }

    private static bool CreateJpegCopy(
        string originalPath,
        PortableMediaItem item,
        uint quality,
        IProgress<TransferProgress>? progress,
        int processedFiles,
        int totalFiles,
        long processedBytes,
        long totalBytes,
        int copied,
        int skipped,
        int errors,
        int jpegCreated,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(originalPath))
        {
            throw new FileNotFoundException("The exported HEIC original could not be found.", originalPath);
        }

        var directory = Path.GetDirectoryName(originalPath)!;
        var stem = Path.GetFileNameWithoutExtension(originalPath);
        var jpegPath = Path.Combine(directory, $"{stem}__from_HEIC.jpg");
        if (File.Exists(jpegPath) && new FileInfo(jpegPath).Length > 0)
        {
            return false;
        }

        var temporaryPath = jpegPath + ".partial";
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        progress?.Report(new TransferProgress("Creating high-quality JPEG", item.OriginalFileName, processedFiles, totalFiles, processedBytes, totalBytes, copied, skipped, errors, jpegCreated));

        try
        {
            using var image = new MagickImage(originalPath);
            image.AutoOrient();
            image.Format = MagickFormat.Jpeg;
            image.Quality = Math.Clamp(quality, 90u, 100u);
            image.Write(temporaryPath);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, jpegPath, overwrite: false);
            ApplyCaptureTimestamps(jpegPath, item.CaptureTime);
            return true;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string CreateJpegOnly(
        string heicTemporaryPath,
        string jpegPath,
        PortableMediaItem item,
        uint quality,
        IProgress<TransferProgress>? progress,
        int processedFiles,
        int totalFiles,
        long processedBytes,
        long totalBytes,
        int copied,
        int skipped,
        int errors,
        int jpegCreated,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var jpegTemporaryPath = jpegPath + ".partial";
        TryDeletePartial(jpegTemporaryPath);

        progress?.Report(new TransferProgress("Converting HEIC to JPEG (quality 100)", item.OriginalFileName, processedFiles, totalFiles, processedBytes, totalBytes, copied, skipped, errors, jpegCreated));

        try
        {
            using (var image = new MagickImage(heicTemporaryPath))
            {
                image.AutoOrient();
                image.Format = MagickFormat.Jpeg;
                image.Quality = Math.Clamp(quality, 90u, 100u);
                image.Write(jpegTemporaryPath);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (new FileInfo(jpegTemporaryPath).Length == 0)
            {
                throw new IOException($"JPEG conversion produced an empty file for {item.OriginalFileName}.");
            }

            using var jpegStream = File.OpenRead(jpegTemporaryPath);
            var jpegHash = Convert.ToHexString(SHA256.HashData(jpegStream));
            jpegStream.Close();
            File.Move(jpegTemporaryPath, jpegPath, overwrite: false);
            ApplyCaptureTimestamps(jpegPath, item.CaptureTime);
            return jpegHash;
        }
        finally
        {
            TryDeletePartial(jpegTemporaryPath);
        }
    }

    private static bool IsHeic(PortableMediaItem item)
        => item.Extension.Equals(".heic", StringComparison.OrdinalIgnoreCase)
           || item.Extension.Equals(".heif", StringComparison.OrdinalIgnoreCase);

    private static void TryDeletePartial(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A disconnected device must not mask the original transfer exception.
        }
    }

    private static void AppendErrorLog(string destinationRoot, PortableMediaItem item, string operation, Exception exception)
    {
        try
        {
            var logPath = Path.Combine(destinationRoot, "iphone-export-errors.log");
            var version = typeof(ExportEngine).Assembly.GetName().Version?.ToString() ?? "unknown";
            var message = $"[{DateTimeOffset.Now:O}] iPhone Archive {version}: {operation} failed for {item.OriginalFileName} ({item.ObjectId}){Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(logPath, message, Encoding.UTF8);
        }
        catch
        {
            // Logging must never stop the remaining transfers.
        }
    }

    private static string PlanTargetPath(string destinationRoot, PortableMediaItem item, ExportOptions options)
    {
        var local = item.CaptureTime.LocalDateTime;
        var folder = options.OrganizeByMonth
            ? Path.Combine(destinationRoot, local.ToString("yyyy"), local.ToString("yyyy-MM"))
            : destinationRoot;

        var original = SanitizeFileName(item.OriginalFileName);
        if (options.HeicMode == HeicExportMode.JpegOnly && IsHeic(item))
        {
            original = Path.ChangeExtension(original, ".jpg");
        }
        var name = options.PrefixCaptureTime ? $"{local:yyyy-MM-dd_HH-mm-ss}_{original}" : original;
        return Path.Combine(folder, name);
    }

    private static string MakeUniquePath(string desiredPath)
    {
        // A stale .partial belongs to an interrupted attempt at this exact target.
        // CopyOne removes it before retrying, so it must not force a __2 filename.
        if (!File.Exists(desiredPath))
        {
            return desiredPath;
        }

        var directory = Path.GetDirectoryName(desiredPath)!;
        var stem = Path.GetFileNameWithoutExtension(desiredPath);
        var extension = Path.GetExtension(desiredPath);
        for (var suffix = 2; suffix < 100_000; suffix++)
        {
            var candidate = Path.Combine(directory, $"{stem}__{suffix}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"Could not choose a unique destination name for {desiredPath}.");
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(fileName.Length);
        foreach (var character in fileName)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }
        return builder.ToString().TrimEnd(' ', '.');
    }

    private static string BuildSourceKey(string deviceId, PortableMediaItem item, HeicExportMode heicMode)
    {
        var stablePart = item.SelectionKey;
        if (IsHeic(item))
        {
            stablePart = $"{stablePart}|heic-mode:{heicMode}";
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{deviceId}|{stablePart}")));
    }

    private static void ApplyCaptureTimestamps(string path, DateTimeOffset captureTime)
    {
        try
        {
            File.SetCreationTimeUtc(path, captureTime.UtcDateTime);
            File.SetLastWriteTimeUtc(path, captureTime.UtcDateTime);
        }
        catch
        {
            // Some network and removable destinations do not support Windows timestamps.
        }
    }

    private sealed class WpdSessionHolder : IDisposable
    {
        private readonly string _deviceId;

        public WpdSessionHolder(string deviceId)
        {
            _deviceId = deviceId;
            Current = WpdClient.Open(deviceId);
        }

        public WpdClient Current { get; private set; }

        public void Reset()
        {
            Current.Dispose();
            Thread.Sleep(400);
            Current = WpdClient.Open(_deviceId);
        }

        public void Dispose() => Current.Dispose();
    }

    private sealed class DeviceStreamMismatchException : IOException
    {
        public DeviceStreamMismatchException(string message) : base(message)
        {
        }
    }
}

