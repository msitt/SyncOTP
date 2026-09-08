using System.Text;

namespace SyncOTP.Core;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// Deliberately tiny rolling file logger. The app is a single process with very low log volume,
/// so a full logging framework would be more dependency than it is worth.
/// Message bodies are only ever written at <see cref="LogLevel.Debug"/>, which is off by default.
/// </summary>
public static class FileLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 1_000_000;
    private const int KeepFiles = 3;

    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    /// <summary>Raised for every written line so the UI can surface warnings/errors.</summary>
    public static event Action<LogLevel, string>? Logged;

    public static string CurrentFile => Path.Combine(Paths.LogDir, "syncotp.log");

    public static void Debug(string m) => Write(LogLevel.Debug, m);
    public static void Info(string m) => Write(LogLevel.Info, m);
    public static void Warn(string m) => Write(LogLevel.Warn, m);
    public static void Error(string m, Exception? ex = null) =>
        Write(LogLevel.Error, ex is null ? m : $"{m}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(LogLevel level, string message)
    {
        if (level < MinLevel) return;

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level.ToString().ToUpperInvariant(),-5}] {message}";
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.LogDir);
                Roll();
                File.AppendAllText(CurrentFile, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }

        try { Logged?.Invoke(level, message); } catch { }
    }

    private static void Roll()
    {
        var info = new FileInfo(CurrentFile);
        if (!info.Exists || info.Length < MaxBytes) return;

        var oldest = Path.Combine(Paths.LogDir, $"syncotp.{KeepFiles}.log");
        if (File.Exists(oldest)) File.Delete(oldest);

        for (var i = KeepFiles - 1; i >= 1; i--)
        {
            var from = Path.Combine(Paths.LogDir, $"syncotp.{i}.log");
            var to = Path.Combine(Paths.LogDir, $"syncotp.{i + 1}.log");
            if (File.Exists(from)) File.Move(from, to, overwrite: true);
        }

        File.Move(CurrentFile, Path.Combine(Paths.LogDir, "syncotp.1.log"), overwrite: true);
    }
}
