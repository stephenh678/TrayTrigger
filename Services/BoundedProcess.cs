using System.Diagnostics;

namespace TrayTrigger.Services;

/// <summary>
/// Runs a console tool and collects its output within a time limit. Both streams are read as the
/// tool writes, so a full pipe can't stall it, and the wait is what the limit measures: a tool that
/// hangs is killed at the limit instead of holding the caller.
/// </summary>
internal static class BoundedProcess
{
    internal readonly record struct Result(bool Completed, int ExitCode, string Output, string Error);

    public static Result? Run(ProcessStartInfo psi, int timeoutMs)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using var proc = Process.Start(psi);
        if (proc == null) return null;

        var output = proc.StandardOutput.ReadToEndAsync();
        var error = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
            return new Result(false, -1, string.Empty, string.Empty);
        }

        // The process has exited; the streams close with it, so this returns at once.
        proc.WaitForExit();
        return new Result(true, proc.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }
}
