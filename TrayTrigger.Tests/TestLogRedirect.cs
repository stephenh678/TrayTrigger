using System;
using System.IO;
using System.Runtime.CompilerServices;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// Sends this run's logging to a throwaway file. Without it the suite logs into
/// %LocalAppData%\TrayTrigger\debug.log - the developer's own log, and the file testers are asked
/// to send - at roughly 170 lines a run, and a long enough run could trip the 5 MB rotation and
/// archive their real history off to debug.old.log.
///
/// A module initializer rather than a fixture: it runs when the test assembly loads, before any
/// test can construct a service that logs on the way up.
/// </summary>
internal static class TestLogRedirect
{
    [ModuleInitializer]
    internal static void RedirectLogToTempFile()
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "TrayTriggerTestLogs");
            Directory.CreateDirectory(dir);
            LoggingService.UseLogFileForTests(Path.Combine(dir, $"test-{Environment.ProcessId}.log"));
        }
        catch
        {
            // A test run must not fail because the redirect could not be set up; worst case the
            // suite logs where it always did.
        }
    }
}
