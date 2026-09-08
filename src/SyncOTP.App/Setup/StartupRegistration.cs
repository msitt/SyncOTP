using Microsoft.Win32;
using SyncOTP.Core;

namespace SyncOTP.App.Setup;

/// <summary>
/// Per-user "start with Windows" via the HKCU Run key. No admin rights, no scheduled task, and the
/// user can see and remove it from Task Manager's Startup tab.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SyncOTP";

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SyncOTP.exe");

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Contains("SyncOTP", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            FileLog.Debug($"could not read the Run key: {ex.Message}");
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return;

            if (enabled)
            {
                key.SetValue(ValueName, $"\"{ExecutablePath}\"");
                FileLog.Info($"start with Windows enabled: {ExecutablePath}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                FileLog.Info("start with Windows disabled");
            }
        }
        catch (Exception ex)
        {
            FileLog.Error("could not update the Run key", ex);
        }
    }
}
