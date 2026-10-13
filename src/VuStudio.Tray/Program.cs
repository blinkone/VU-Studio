using Microsoft.Extensions.Logging;
using VuStudio.Session.Logging;

namespace VuStudio.Tray;

internal static class Program
{
    /// <summary>
    /// VuStudio.exe: the desktop app and user-session agent. <c>--background</c> starts in the tray without
    /// opening the window (used by the Run key); <c>--port N</c> overrides the service port.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        var options = TrayOptions.Parse(args);
        var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            return 0; // the running instance was asked to show its window
        }

        using var loggers = new FileLoggerFactory(TrayPaths.Logs, "tray");
        var log = loggers.CreateLogger("Program");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => log.LogError(e.Exception, "Unhandled UI exception");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.LogWarning(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        try
        {
            ApplicationConfiguration.Initialize();
            using var context = new TrayApplicationContext(options, loggers, instance);
            Application.Run(context);
            return 0;
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "The desktop app failed");
            return 1;
        }
        finally
        {
            instance.Dispose();
            log.LogInformation("Exited");
        }
    }
}
