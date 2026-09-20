using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace TrayTrigger.Services;

public enum LogLevel
{
    Info,
    Warning,
    Error,
    Verbose
}

public static class LoggingService
{
    private static readonly Lock LockObj = new();

    /// <summary>
    /// Serialises writes across processes, not just threads. Two TrayTrigger processes overlap
    /// routinely - launching the app while it is already running signals the first instance and
    /// exits, and an in-app update runs the new build beside the old one - and they share one
    /// log file. FileMode.Append tracks a position per stream rather than appending atomically,
    /// so without this each process writes over the other's bytes: a two-process test lost half
    /// of 6000 lines outright, which is how Preston's log ended up with a line whose first half
    /// had been overwritten ("p] Application starting...") and timestamps that ran backwards.
    /// </summary>
    private static readonly Mutex FileMutex = new(false, @"Local\TrayTrigger_DebugLog");
    private static string? _logFilePath;
    private static bool _isVerboseEnabled;
    private static StreamWriter? _writer;
    private static int _writesSinceRotationCheck;
    private static bool _bannerWrittenThisProcess;
    private static bool _logFileCreatedThisProcess;
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
                    try { File.Copy(legacyPath, newPath, overwrite: false); } catch { /* the old log is a convenience, not a requirement */ }
                }

                _logFilePath = newPath;
            }
            return _logFilePath;
        }
    }

    /// <summary>
    /// Test seam. The log path is otherwise fixed at %LocalAppData%\TrayTrigger\debug.log, so a
    /// test run wrote straight into the real user's log - the very file testers are asked to
    /// send - at roughly 170 lines a run, and could trip rotation and archive their history away.
    /// TrayTrigger.Tests calls this from a module initializer, before any test touches a service
    /// that logs.
    /// </summary>
    internal static void UseLogFileForTests(string path)
    {
        lock (LockObj)
        {
            CloseWriter();
            _logFilePath = path;
            _bannerWrittenThisProcess = false;
            _logFileCreatedThisProcess = false;
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

        // One banner per run. A tester's log file outlives many versions - Preston's covered
        // 1.0.2 through 1.4.0 over six days - and without this there is nothing in it saying
        // which build wrote which line. Skipped when the file was just created, because its
        // own header says the same thing.
        lock (LockObj)
        {
            if (!_bannerWrittenThisProcess)
            {
                _bannerWrittenThisProcess = true;
                if (!_logFileCreatedThisProcess)
                {
                    AppendLine(BuildBanner("session started"));
                }
            }
        }

        Info("App", $"TrayTrigger logging initialized. Verbose={_isVerboseEnabled}");
    }

    /// <summary>
    /// The running build, as baked in by CI (<c>/p:Version=1.4.1-beta.1</c>). The SDK appends
    /// "+&lt;commit&gt;" to the informational version; a tester reading their own log does not
    /// need it.
    /// </summary>
    private static string AppVersion()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            string? informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                int plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }

            return assembly.GetName().Version?.ToString() ?? "unknown";
        }
        catch
        {
            // The logger cannot log its own failure, and must never take the app down.
            return "unknown";
        }
    }

    /// <summary>
    /// Every block that starts or restarts the file says the same things, so a log that has been
    /// rotated - or a fragment someone pastes into chat - still names the build and the machine
    /// it came from.
    /// </summary>
    private static string BuildBanner(string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine($" TrayTrigger {AppVersion()} - {title} {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($" OS: {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})");
        sb.AppendLine($" Machine: {Environment.MachineName} | User: {Environment.UserName}");
        sb.AppendLine($" .NET Runtime: {Environment.Version}");
        sb.AppendLine("================================================================================");
        return sb.ToString();
    }

    /// <summary>
    /// Returns whether the caller now owns the mutex and must release it. An abandoned mutex
    /// means the previous holder died mid-write; ownership transfers to us and the file may be
    /// short a line, which is not worth failing over.
    /// </summary>
    private static bool AcquireFileMutex()
    {
        try
        {
            return FileMutex.WaitOne(TimeSpan.FromSeconds(2));
        }
        catch (AbandonedMutexException)
        {
            // The previous holder died mid-write: ownership passes to us.
            return true;
        }
        catch
        {
            // The logger cannot log its own failure, and must never take the app down.
            return false;
        }
    }

    private static void ReleaseFileMutex(bool held)
    {
        if (!held) return;
        try { FileMutex.ReleaseMutex(); } catch { /* the logger cannot log its own failure, and must never take the app down */ }
    }

    /// <summary>
    /// Appends one already-formatted line. The seek matters: another process may have extended
    /// the file since this stream last wrote, and without it this write would land on top of
    /// whatever that process put there.
    /// </summary>
    private static void AppendLine(string line)
    {
        bool held = AcquireFileMutex();
        try
        {
            var writer = GetWriter();
            writer.Flush();
            writer.BaseStream.Seek(0, SeekOrigin.End);
            writer.Write(line);
            writer.Flush();
        }
        finally
        {
            ReleaseFileMutex(held);
        }
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
                // Under the cross-process mutex as well: these replace the whole file, and doing
                // that while another process is mid-append truncates its output.
                bool held = AcquireFileMutex();
                try
                {
                    CloseWriter();
                    if (!File.Exists(LogFilePath))
                    {
                        _logFileCreatedThisProcess = true;
                        File.WriteAllText(LogFilePath, BuildBanner("debug log started"));
                    }
                    else
                    {
                        // Rotate if log file exceeds 5MB
                        var fi = new FileInfo(LogFilePath);
                        if (fi.Length > 5 * 1024 * 1024)
                        {
                            string oldLog = Path.Combine(Path.GetDirectoryName(LogFilePath)!, "debug.old.log");
                            File.Copy(LogFilePath, oldLog, overwrite: true);
                            File.WriteAllText(LogFilePath, BuildBanner("log rotated, previous log archived to debug.old.log"));
                        }
                    }
                }
                finally
                {
                    ReleaseFileMutex(held);
                }
            }
        }
        catch { /* the logger cannot log its own failure, and must never take the app down */ }
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

    /// <summary>
    /// An exception that was caught and deliberately not acted on. Verbose only: these are the
    /// expected misses - a process that exited between two calls, a registry value that is not
    /// there, a file another program holds - and a tester's ordinary log should not fill with
    /// them. But a catch that says nothing at all makes "it failed quietly" indistinguishable from
    /// "it never ran", which is how a log ends up unable to answer the question it was sent for.
    /// See CONTRIBUTING.md, Logging.
    /// </summary>
    /// <param name="doing">What was being attempted, in a few words, when the name of the calling
    /// member does not already say it.</param>
    public static void Swallowed(string category, Exception ex, string? doing = null,
        [System.Runtime.CompilerServices.CallerMemberName] string member = "")
    {
        if (!_isVerboseEnabled) return;
        Log(LogLevel.Verbose, category, doing == null
            ? $"{member}: ignored {ex.GetType().Name}: {ex.Message}"
            : $"{member}: {doing} - ignored {ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>
    /// Something the user was just shown: a status-bar line, a dialog, a toast, the launch popup.
    /// Verbose only. Without it a log says what TrayTrigger did and never what the person saw -
    /// "No new games found" five times in five seconds left no trace at all - so every surface
    /// that puts words in front of the user echoes them here, at the one place it sets them.
    /// </summary>
    /// <param name="surface">Where it appeared: "Status", "Dialog", "Toast", "Launch popup".</param>
    public static void Shown(string surface, string? text)
    {
        if (!_isVerboseEnabled || string.IsNullOrWhiteSpace(text)) return;
        Log(LogLevel.Verbose, "UI", $"{surface}: {text.ReplaceLineEndings(" ")}");
    }

    public static void Log(LogLevel level, string category, string message)
    {
        if (level == LogLevel.Verbose && !_isVerboseEnabled)
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
                    _ => "VERB "
                };

                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{tag}] [{category}] {message}\n";
                AppendLine(line);

                if (++_writesSinceRotationCheck >= RotationCheckInterval)
                {
                    _writesSinceRotationCheck = 0;
                    CheckRotation();
                }
            }
        }
        catch { /* the logger cannot log its own failure, and must never take the app down */ }
    }

    // Must be called while holding LockObj.
    private static void CheckRotation()
    {
        try
        {
            var fi = new FileInfo(LogFilePath);
            if (fi.Exists && fi.Length > MaxLogSizeBytes)
            {
                bool held = AcquireFileMutex();
                try
                {
                    CloseWriter();
                    string oldLog = Path.Combine(Path.GetDirectoryName(LogFilePath)!, "debug.old.log");
                    File.Copy(LogFilePath, oldLog, overwrite: true);
                    File.WriteAllText(LogFilePath, BuildBanner("log rotated, previous log archived to debug.old.log"));
                }
                finally
                {
                    ReleaseFileMutex(held);
                }
            }
        }
        catch { /* the logger cannot log its own failure, and must never take the app down */ }
    }

    public static void ClearLog()
    {
        try
        {
            lock (LockObj)
            {
                bool held = AcquireFileMutex();
                try
                {
                    CloseWriter();
                    File.WriteAllText(LogFilePath, BuildBanner("debug log cleared & restarted"));
                }
                finally
                {
                    ReleaseFileMutex(held);
                }
            }
        }
        catch { /* the logger cannot log its own failure, and must never take the app down */ }
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
        catch { /* the logger cannot log its own failure, and must never take the app down */ }
        return "0 KB";
    }

    public static void OpenLogInEditor()
    {
        try
        {
            EnsureLogFileExists();
            Process.Start(new ProcessStartInfo(LogFilePath) { UseShellExecute = true });
        }
        catch { /* the logger cannot log its own failure, and must never take the app down */ }
    }
}
