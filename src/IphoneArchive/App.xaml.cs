using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace IphoneArchive;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ConfigureImageMemoryLimits();
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog("AppDomain unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
    }

    private static void ConfigureImageMemoryLimits()
    {
        // ImageMagick can otherwise reserve memory proportional to a very large
        // ProRAW/HEIC image. Disk mapping keeps conversion bounded on modest PCs.
        var cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IphoneArchive",
            "ImageCache");
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            Environment.SetEnvironmentVariable("MAGICK_MEMORY_LIMIT", "512MiB");
            Environment.SetEnvironmentVariable("MAGICK_MAP_LIMIT", "1GiB");
            Environment.SetEnvironmentVariable("MAGICK_THREAD_LIMIT", "2");
            Environment.SetEnvironmentVariable("MAGICK_TEMPORARY_PATH", cacheDirectory);
        }
        catch
        {
            // Conversion remains available with ImageMagick defaults.
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var path = WriteCrashLog("UI dispatcher exception", e.Exception);
        e.Handled = true;
        MessageBox.Show(
            $"iPhone Archive encountered an unexpected error and must close.\n\nA diagnostic log was written to:\n{path}",
            "iPhone Archive error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown(-1);
    }

    private static string WriteCrashLog(string category, Exception? exception)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IphoneArchive");
        var path = Path.Combine(directory, "crash.log");
        try
        {
            Directory.CreateDirectory(directory);
            var version = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown";
            var report = new StringBuilder()
                .Append('[').Append(DateTimeOffset.Now.ToString("O")).Append("] iPhone Archive ").Append(version).AppendLine()
                .AppendLine(category)
                .AppendLine(exception?.ToString() ?? "No managed exception object was available.")
                .AppendLine()
                .ToString();
            File.AppendAllText(path, report, Encoding.UTF8);
        }
        catch
        {
            // Crash reporting must not trigger another exception.
        }
        return path;
    }
}

