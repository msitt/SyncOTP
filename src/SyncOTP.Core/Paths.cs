namespace SyncOTP.Core;

/// <summary>Single source of truth for where SyncOTP keeps its files.</summary>
public static class Paths
{
    /// <summary>%APPDATA%\SyncOTP, roaming, holds config.json.</summary>
    public static string ConfigDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SyncOTP");

    public static string ConfigFile => Path.Combine(ConfigDir, "config.json");

    /// <summary>%LOCALAPPDATA%\SyncOTP, machine-local, holds logs and state.</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SyncOTP");

    public static string LogDir => Path.Combine(DataDir, "logs");

    /// <summary>Staging for a downloaded release. Created on demand, not by EnsureCreated.</summary>
    public static string UpdateDir => Path.Combine(DataDir, "updates");

    /// <summary>Written by the update helper script, so a failed update is diagnosable.</summary>
    public static string UpdateLogFile => Path.Combine(LogDir, "update.log");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(LogDir);
    }
}
