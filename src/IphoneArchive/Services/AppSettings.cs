using System.Text.Json;
using IphoneArchive.Models;

namespace IphoneArchive.Services;

public sealed record AppSettings(string Destination, bool OrganizeByMonth, bool PrefixCaptureTime, bool VerifyCopies, HeicExportMode HeicMode)
{
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IphoneArchive",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? Default();
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty(nameof(HeicMode), out _))
            {
                return settings;
            }

            var oldDualMode = document.RootElement.TryGetProperty("CreateJpegCopies", out var oldValue)
                              && oldValue.ValueKind == JsonValueKind.True;
            return settings with { HeicMode = oldDualMode ? HeicExportMode.HeicAndJpeg : HeicExportMode.JpegOnly };
        }
        catch
        {
            return Default();
        }
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static AppSettings Default()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return new AppSettings(Path.Combine(pictures, "iPhone Archive"), true, true, true, HeicExportMode.JpegOnly);
    }
}

