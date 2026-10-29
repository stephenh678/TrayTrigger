using System.Diagnostics;
using System.Threading.Tasks;

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

        // The streams normally close with the process, but a grandchild it started can hold them open
        // (powershell leaving something running), so the read is bounded too rather than waiting for EOF.
        bool drained = Task.WaitAll([output, error], DrainTimeoutMs);
        return new Result(true, proc.ExitCode,
            drained || output.IsCompletedSuccessfully ? output.Result : string.Empty,
            drained || error.IsCompletedSuccessfully ? error.Result : string.Empty);
    }

    /// <summary>How long to wait for the output after the process has exited.</summary>
    internal const int DrainTimeoutMs = 2000;
}
