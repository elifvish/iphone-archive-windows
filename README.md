# iPhone Archive for Windows

> Independent open development project. Not affiliated with or endorsed by Apple Inc.

A professional, read-only Windows photo-library manager for copying locally available iPhone photos and videos over USB. It keeps originals, orders files by capture time, verifies completed copies, and records successful transfers so interrupted jobs can safely be resumed.

## Version 2.0 highlights

- Detects iPhones and other Windows Portable Devices (WPD).
- Scans the device iteratively to avoid recursion failures on malformed device trees.
- Provides a fast, virtualized photo-library browser with filename search, media filters, metrics, select-all and per-item export selection.
- Reuses a completed library scan when export starts instead of querying the iPhone twice.
- Supports HEIC originals, dual HEIC + JPEG, or JPEG-only export while preserving MOV, MP4, ProRAW/DNG, and Live Photo videos.
- Sorts by the device's capture/created timestamp and creates `YYYY\YYYY-MM` folders.
- Optionally prefixes names with `YYYY-MM-DD_HH-mm-ss_`.
- Writes each file to a temporary `.partial` file, hashes it, optionally verifies it from disk, and only then moves it into place.
- Removes failed `.partial` files and records per-file diagnostics in `iphone-export-errors.log`.
- Resets and retries the WPD connection when an iPhone read fails or returns a stream whose size does not match its metadata.
- Uses pooled, bounded USB buffers and throttled progress events to prevent memory growth during large transfers.
- Limits HEIC/ProRAW conversion memory and spills large ImageMagick working data to a local disk cache.
- Records unexpected managed crashes in `%LOCALAPPDATA%\IphoneArchive\crash.log` and shows that path before closing.
- Stores completed-transfer records in `.iphone-export.db` in the destination, so rerunning skips completed files.
- Never writes to or deletes from the iPhone.

## Requirements

- Windows 10 (19041+) or Windows 11.
- A data-capable USB cable.
- The iPhone must be unlocked and must trust the PC.
- Full-resolution originals must be present on the phone. Items that exist only in iCloud may not be exposed over USB.
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build. The published application is self-contained.

## Build

On Windows, double-click `build-windows.cmd`, or run:

```powershell
dotnet publish .\src\IphoneArchive\IphoneArchive.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The executable will be at `publish\win-x64\IphoneArchive.exe`.

GitHub Actions also builds the self-contained Windows executable on every push to `main` and makes it available as a workflow artifact.

## Use

1. Connect the iPhone, unlock it, and tap **Trust** if prompted.
2. Open iPhone Archive and click **Refresh devices**.
3. Scan the photo library and select the items you want.
4. Open **Export & backup**, choose an archive folder and click **Start verified export**.
5. If a cable disconnects or Windows restarts, reconnect the phone and run the same export again. Completed items are skipped.

## HEIC and JPEG

The exporter deliberately keeps the stream Windows receives from the iPhone. For the most reliable archival copy, set the iPhone's **Settings > Apps > Photos > Transfer to Mac or PC** option to **Keep Originals**. Apple's **Automatic** option may convert HEIC to JPEG and HEVC to H.264 during a compatible USB transfer, but Apple does not expose a quality control for that conversion.

Choose one of three HEIC modes:

- **Original HEIC only** keeps the original without creating a JPEG.
- **HEIC + JPEG** keeps the original and adds a `__from_HEIC.jpg` copy.
- **JPEG only — no HEIC copied** temporarily downloads the HEIC, converts it, verifies the result, and deletes the HEIC temporary file before recording the transfer as complete.

JPEG modes use the encoder's maximum quality setting of 100, preserve embedded profiles where supported, and apply the same capture timestamp to the Windows file. JPEG is inherently lossy even at quality 100; choose an HEIC-preserving mode when archival fidelity matters.

## Notes

- Windows Portable Devices reports the source timestamp. The app uses that value for sorting and Windows file dates. Some devices may expose a less precise date than the embedded EXIF/QuickTime timestamp.
- Album order, favorites, hidden state, and Photos edit history are not fully exposed through standard USB media access.
- `.AAE` edit sidecars are excluded because they are not viewable media and some iPhone WPD drivers enumerate them but reject their data streams.
- The SQLite manifest is hidden by the app when Windows permits it. Keep it with the export if you want future runs to skip already copied files.

## Broader device-manager roadmap

An iMazing/CopyTrans-class device manager is a suite of separate Apple protocols, not one large photo importer. Version 2.0 intentionally ships the verified photo module first. Future modules can be added without putting the archive path at risk:

1. Encrypted full-device backup and restore using Apple's mobile backup protocol.
2. Backup browser for Messages, contacts, call history, voicemail, notes, and files.
3. Music, ringtone, books, and app-document transfer.
4. Device diagnostics, battery health, update/recovery guidance, and backup snapshots.
5. Supervised-device and fleet features where Apple exposes supported management APIs.

The product should provide comparable user outcomes through original code and UI; it should not copy iMazing's proprietary code, assets, branding, or exact interface.

## Repository status

This repository contains original source code and project assets. No open-source license has been selected yet, so public visibility does not by itself grant permission to redistribute or modify the code. Add a license before inviting third-party contributions.

