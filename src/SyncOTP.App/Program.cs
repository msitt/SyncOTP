using SyncOTP.Core;

namespace SyncOTP.App;

internal static class Program
{
    private const string MutexName = @"Local\SyncOTP.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        Paths.EnsureCreated();

        var config = Config.Load(out var createdNew);
        FileLog.MinLevel = config.VerboseLogging ? Core.LogLevel.Debug : Core.LogLevel.Info;

        // Convenience for setting the phone up: print the header the Shortcut needs and quit.
        if (args.Any(a => a.Equals("--print-auth-header", StringComparison.OrdinalIgnoreCase)))
            return PrintAuthHeader(config);

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            FileLog.Info("another instance is already running; exiting");
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => FileLog.Error("unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            FileLog.Error("unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            FileLog.Error("unobserved task exception", e.Exception);
            e.SetObserved();
        };

        FileLog.Info($"SyncOTP v{AppVersion.Display} starting from {Environment.ProcessPath}");

        try
        {
            Application.Run(new TrayContext(config, createdNew));
        }
        catch (Exception ex)
        {
            FileLog.Error("fatal error", ex);
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Writes the Authorization header value to stdout. A WinExe has no console of its own, so this
    /// attaches to the parent one when launched from a terminal.
    /// </summary>
    private static int PrintAuthHeader(Config config)
    {
        NativeMethods.AttachParentConsole();

        if (!config.Ntfy.HasCredentials)
        {
            Console.Error.WriteLine(
                $"No ntfy credentials set in {Paths.ConfigFile}; the Shortcut needs no Authorization header.");
            return 1;
        }

        Console.WriteLine(config.Ntfy.BuildBasicAuthHeader());
        Console.Out.Flush();
        return 0;
    }
}

internal static partial class NativeMethods
{
    private const int AttachParentProcess = -1;

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    public static void AttachParentConsole()
    {
        try { AttachConsole(AttachParentProcess); } catch { }
    }
}
