using System;
using System.IO;
using System.Text;
using System.Windows;

namespace TrayTrigger.Services;

/// <summary>
/// What is left behind when TrayTrigger fails to start or stops unexpectedly: one small file, <c>crash.log</c> beside the
/// debug log, written whatever the logging setting is and without depending on the logger having started. A failure
/// before the logger and the exception handlers were set up used to leave nothing but a Windows Event Log entry, and an
/// exception that reached the dispatcher handler while the app was still starting left a running process with no window
/// and no tray icon, which the next launch then handed over to.
///
/// <para>The message a person sees says where the file is, so they can send it with a report.</para>
/// </summary>
internal static class CrashReport
{
    private const long MaxBytes = 256 * 1024;

    private static string? _filePath;

    /// <summary>
    /// True for a capture or a test run, which nobody is watching: the report is still written, but no message box is shown
    /// (it would wait for a click that never comes).
    /// </summary>
    internal static bool Quiet { get; set; }

    internal static string FilePath
    {
        get
        {
            _filePath ??= Path.Combine(Path.GetDirectoryName(LoggingService.LogFilePath) ?? Path.GetTempPath(), "crash.log");
            return _filePath;
        }
    }

    /// <summary>Test seam, like <see cref="LoggingService.UseLogFileForTests"/>: a test run must not write the developer's own crash.log.</summary>
    internal static void UseFileForTests(string path) => _filePath = path;

    /// <summary>
    /// Appends one report: when and where it happened (<paramref name="where"/>), the build and machine, and the exception in full.
    /// A file past 256 KB starts again, so repeated failures cannot grow it without end. Returns the file, or null when it could
    /// not be written; never throws, since it runs when something is already wrong.
    /// </summary>
    internal static string? Write(string where, object? error)
    {
        try
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Delete(path);

            var text = new StringBuilder();
            text.Append(LoggingService.BuildBanner($"crash ({where})"));
            text.AppendLine(error?.ToString() ?? "(no exception was given)");
            text.AppendLine();
            File.AppendAllText(path, text.ToString(), Encoding.UTF8);
            return path;
        }
        catch
        {
            // Nothing else can be done about a crash report that cannot be written.
            return null;
        }
    }

    /// <summary>What the message box says: that it stopped, and where the details are.</summary>
    internal static string Message(bool atStart, string? file) =>
        (atStart ? "TrayTrigger could not start." : "TrayTrigger stopped unexpectedly.")
        + (file == null
            ? "\n\nThe details could not be saved."
            : "\n\nThe details were saved to:\n" + file + "\n\nIf it keeps happening, please send that file with your report.");

    /// <summary>Tells the person it failed. Does nothing for a capture or a test run (<see cref="Quiet"/>).</summary>
    internal static void Show(bool atStart, string? file)
    {
        if (Quiet) return;
        try
        {
            // The plain Windows message box on purpose: the themed dialogs need the app's resources, which may be what failed.
            MessageBox.Show(Message(atStart, file), "TrayTrigger", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // The message is a courtesy; the report is already on disk.
        }
    }

    /// <summary>
    /// Puts the last-chance handler in place, as early as the app can: an exception that nothing caught ends the process, and
    /// this writes it down and says so first. Exceptions on the UI thread are the app's own handler's business once it is
    /// running; this catches the ones on other threads, and any before that handler exists.
    /// </summary>
    internal static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            string? file = Write("unhandled", args.ExceptionObject);
            try { LoggingService.Error("App", $"FATAL AppDomain Exception: {args.ExceptionObject}"); }
            catch { /* the report above is the one that matters */ }
            Show(atStart: false, file);
        };
    }
}
