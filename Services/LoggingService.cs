using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace TrayTrigger.Services;

public enum LogLevel
{
    Info,
    Warning,
    Error,
    Debug,
    Verbose
}

public static class LoggingService
{
    private static readonly Lock LockObj = new();
    private static string? _logFilePath;
    private static bool _isVerboseEnabled;
    private static StreamWriter? _writer;
    private static int _writesSinceRotationCheck;
    private const int RotationCheckInterval = 50;
    private const long MaxLogSizeBytes = 5 * 1024 * 1024;

    public static string LogFilePath
    {
        get
        {
            if (_logFilePath == null)
            {
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string dir = Path.Combine(localApp, "TrayTrigger");
                Directory.CreateDirectory(dir);
                string newPath = Path.Combine(dir, "debug.log");

                // Transparently migrate legacy log from Documents\TrayTrigger if present
                string legacyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TrayTrigger", "debug.log");
                if (!File.Exists(newPath) && File.Exists(legacyPath))
                {
                    try { File.Copy(legacyPath, newPath, overwrite: false); } catch { }
                }

                _logFilePath = newPath;
            }
            return _logFilePath;
        }
    }

    public static bool IsVerboseEnabled
    {
        get => _isVerboseEnabled;
        set => _isVerboseEnabled = value;
    }

    public static void Initialize(bool verbose)
    {
        _isVerboseEnabled = verbose;
        EnsureLogFileExists();
        Info("App", $"TrayTrigger logging initialized. Verbose={_isVerboseEnabled}");
    }

    private static StreamWriter GetWriter()
    {
        if (_writer == null)
        {
            var fs = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(fs) { AutoFlush = true };
        }
        return _writer;
    }

    private static void CloseWriter()
    {
        _writer?.Dispose();
        _writer = null;
    }

    public static void EnsureLogFileExists()
    {
        try
        {
            lock (LockObj)
            {
                CloseWriter();
                if (!File.Exists(LogFilePath))
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("================================================================================");
                    sb.AppendLine($" TrayTrigger Debug Log - Started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($" OS: {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})");
                    sb.AppendLine($" Machine: {Environment.MachineName} | User: {Environment.UserName}");
                    sb.AppendLine($" .NET Runtime: {Environment.Version}");
                    sb.AppendLine("================================================================================");
                    File.WriteAllText(LogFilePath, sb.ToString());
                }
                else
                {
                    // Rotate if log file exceeds 5MB
                    var fi = new FileInfo(LogFilePath);
                    if (fi.Length > 5 * 1024 * 1024)
                    {
                        string oldLog = Path.Combine(Path.GetDirectoryName(LogFilePath)!, "debug.old.log");
                        File.Copy(LogFilePath, oldLog, overwrite: true);
                        File.WriteAllText(LogFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Log rotated. Previous log archived to debug.old.log.\n");
                    }
                }
            }
        }
        catch { }
    }

    public static void Info(string category, string message) => Log(LogLevel.Info, category, message);
    public static void Warn(string category, string message) => Log(LogLevel.Warning, category, message);
    public static void Error(string category, string message, Exception? ex = null) =>
        Log(LogLevel.Error, category, ex != null ? $"{message}\nException: {ex}" : message);

    public static void Verbose(string category, string message)
    {
        if (_isVerboseEnabled)
        {
            Log(LogLevel.Verbose, category, message);
        }
    }

    public static void Debug(string category, string message)
    {
        if (_isVerboseEnabled)
        {
            Log(LogLevel.Debug, category, message);
        }
    }

    public static void Log(LogLevel level, string category, string message)
    {
        if ((level == LogLevel.Verbose || level == LogLevel.Debug) && !_isVerboseEnabled)
        {
            return;
        }

        try
        {
            lock (LockObj)
            {
                string tag = level switch
                {
                    LogLevel.Info => "INFO ",
                    LogLevel.Warning => "WARN ",
                    LogLevel.Error => "ERROR",
                    LogLevel.Verbose => "VERB ",
                    _ => "DEBUG"
                };

                string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{tag}] [{category}] {message}\n";
                GetWriter().Write(line);

                if (++_writesSinceRotationCheck >= RotationCheckInterval)
                {
                    _writesSinceRotationCheck = 0;
                    CheckRotation();
                }
            }
        }
        catch { }
    }

    // Must be called while holding LockObj.
    private static void CheckRotation()
    {
        try
        {
            var fi = new FileInfo(LogFilePath);
            if (fi.Exists && fi.Length > MaxLogSizeBytes)
            {
                CloseWriter();
                string oldLog = Path.Combine(Path.GetDirectoryName(LogFilePath)!, "debug.old.log");
                File.Copy(LogFilePath, oldLog, overwrite: true);
                File.WriteAllText(LogFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Log rotated. Previous log archived to debug.old.log.\n");
            }
        }
        catch { }
    }

    public static void ClearLog()
    {
        try
        {
            lock (LockObj)
            {
                CloseWriter();
                var sb = new StringBuilder();
                sb.AppendLine("================================================================================");
                sb.AppendLine($" TrayTrigger Debug Log - Cleared & Restarted {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($" OS: {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})");
                sb.AppendLine("================================================================================");
                File.WriteAllText(LogFilePath, sb.ToString());
            }
        }
        catch { }
    }

    public static string GetLogFileSizeDisplay()
    {
        try
        {
            if (File.Exists(LogFilePath))
            {
                long bytes = new FileInfo(LogFilePath).Length;
                if (bytes < 1024) return $"{bytes} B";
                if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
                return $"{bytes / (1024.0 * 1024.0):F2} MB";
            }
        }
        catch { }
        return "0 KB";
    }

    public static void OpenLogInEditor()
    {
        try
        {
            EnsureLogFileExists();
            Process.Start(new ProcessStartInfo(LogFilePath) { UseShellExecute = true });
        }
        catch { }
    }
}
