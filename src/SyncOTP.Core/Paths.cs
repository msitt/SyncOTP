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

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(LogDir);
    }
}
