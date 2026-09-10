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

        // Proof that this build actually starts on this machine. The updater runs it against a
        // freshly extracted exe before it is willing to overwrite a working install, which is how a
        // missing runtime turns into a declined update instead of a tray icon that never comes
        // back. It must stay inert, and it must run before the mutex or that check deadlocks
        // against the very instance asking the question.
        if (args.Any(a => a.Equals("--version", StringComparison.OrdinalIgnoreCase)))
            return PrintVersion();

        var afterUpdate = args.Any(a => a.Equals("--updated", StringComparison.OrdinalIgnoreCase));

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance && !TryAcquire(mutex, afterUpdate))
        {
            FileLog.Info("another instance is already running, exiting");
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
        if (afterUpdate) FileLog.Info($"updated to v{AppVersion.Display}");

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
    /// An update relaunches the app the moment the old process exits, and the mutex is not released
    /// until that process is fully gone. Without a short retry the new instance would decide it was
    /// a duplicate and vanish, leaving the user with no app and nothing in the log to explain it.
    /// </summary>
    private static bool TryAcquire(Mutex mutex, bool afterUpdate)
    {
        // A normal second launch keeps its old behaviour of giving up immediately.
        var attempts = afterUpdate ? 10 : 1;
        var timeout = afterUpdate ? TimeSpan.FromMilliseconds(300) : TimeSpan.Zero;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                if (mutex.WaitOne(timeout)) return true;
            }
            catch (AbandonedMutexException)
            {
                // The previous instance died without releasing it, so it is ours now.
                return true;
            }
        }

        return false;
    }

    /// <summary>Writes the version to stdout and quits, without starting the app.</summary>
    private static int PrintVersion()
    {
        NativeMethods.AttachParentConsole();
        Console.WriteLine(AppVersion.Display);
        Console.Out.Flush();
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
