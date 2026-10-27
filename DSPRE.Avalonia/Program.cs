using DSPRE.Core;
using Avalonia;

namespace DSPRE.Avalonia;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--replay-journal")
        {
            return JournalReplayCommand.RunAsync(args).GetAwaiter().GetResult();
        }

        if (BuildCommand.Handles(args))
        {
            if (OperatingSystem.IsWindows()) AttachConsole(-1);
            return BuildCommand.RunAsync(args).GetAwaiter().GetResult();
        }

        StatusLogConsole.Install();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ReportFatal(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        try
        {
            return BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            ReportFatal(ex, "Startup failure");
            throw;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private static void ReportFatal(Exception? exception, string context)
    {
        string message = $"[DSPRE] {context}: {exception}";
        StatusLogConsole.Original.WriteLine(message);
        StatusLog.Error(context, exception?.ToString() ?? "(no exception details)");
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "dspre-crash.log"),
                $"{DateTime.Now:u} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Reporting must never mask the original failure.
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
