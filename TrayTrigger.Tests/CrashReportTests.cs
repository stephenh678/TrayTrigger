using System.IO;
using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>The crash report: what is written when TrayTrigger fails to start or stops, and that writing it never makes things worse.</summary>
public class CrashReportTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tt-crash-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _before = CrashReport.FilePath;
    private string File_ => Path.Combine(_folder, "crash.log");

    public CrashReportTests()
    {
        Directory.CreateDirectory(_folder);
        CrashReport.UseFileForTests(File_);
    }

    public void Dispose()
    {
        CrashReport.UseFileForTests(_before);
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { /* a test folder left in %TEMP% is not worth failing a test over */ }
    }

    [Fact]
    public void AReport_NamesTheBuild_WhereItHappened_AndTheWholeException()
    {
        var error = new InvalidOperationException("outer", new ArgumentException("inner detail"));

        string? path = CrashReport.Write("startup", error);

        Assert.Equal(File_, path);
        string text = File.ReadAllText(path!);
        Assert.Contains("TrayTrigger", text);
        Assert.Contains("crash (startup)", text);
        Assert.Contains("InvalidOperationException: outer", text);
        Assert.Contains("ArgumentException: inner detail", text);
    }

    [Fact]
    public void ReportsAreAddedToTheFile_NotReplaced()
    {
        CrashReport.Write("startup", new Exception("first one"));
        CrashReport.Write("unhandled", new Exception("second one"));

        string text = File.ReadAllText(File_);
        Assert.Contains("first one", text);
        Assert.Contains("second one", text);
        Assert.Contains("crash (startup)", text);
        Assert.Contains("crash (unhandled)", text);
    }

    [Fact]
    public void AFilePastTheLimit_StartsAgain_SoRepeatedFailuresCannotGrowItWithoutEnd()
    {
        File.WriteAllText(File_, new string('x', 300 * 1024));

        CrashReport.Write("startup", new Exception("fresh"));

        string text = File.ReadAllText(File_);
        Assert.Contains("fresh", text);
        Assert.DoesNotContain("xxxxxxxxxx", text);
    }

    [Fact]
    public void NoException_IsSaidSo_NotLeftBlank()
    {
        CrashReport.Write("unhandled", null);

        Assert.Contains("no exception was given", File.ReadAllText(File_));
    }

    [Fact]
    public void AFileThatCannotBeWritten_GivesNull_AndDoesNotThrow()
    {
        // A folder cannot be made inside a file.
        string blocker = Path.Combine(_folder, "blocker");
        File.WriteAllText(blocker, "a file");
        CrashReport.UseFileForTests(Path.Combine(blocker, "crash.log"));

        Assert.Null(CrashReport.Write("startup", new Exception("nowhere to go")));
    }

    [Theory]
    [InlineData(new[] { "--screenshot", "library.png" }, true)]
    [InlineData(new[] { "--screenshot-library", "library.png" }, true)]
    [InlineData(new[] { "--test-dlss" }, true)]
    [InlineData(new[] { "--restore-dlss" }, true)]
    [InlineData(new[] { "--minimized" }, false)]
    [InlineData(new[] { "--theme", "Light" }, false)]
    [InlineData(new string[0], false)]
    public void ARunNobodyIsWatching_ShowsNoMessageBox(string[] args, bool unattended) =>
        Assert.Equal(unattended, TrayTrigger.App.IsUnattendedRun(args));

    [Fact]
    public void TheMessage_SaysWhatHappened_AndWhereTheDetailsAre()
    {
        string start = CrashReport.Message(atStart: true, @"C:\Users\me\AppData\Local\TrayTrigger\crash.log");
        string stop = CrashReport.Message(atStart: false, @"C:\Users\me\AppData\Local\TrayTrigger\crash.log");

        Assert.Contains("could not start", start);
        Assert.Contains("stopped unexpectedly", stop);
        Assert.Contains(@"C:\Users\me\AppData\Local\TrayTrigger\crash.log", start);
        Assert.Contains("send that file", start);
    }

    private sealed class WithStack(string message, string stack) : DllNotFoundException(message)
    {
        public override string? StackTrace => stack;
    }

    [Fact]
    public void WpfsLoaderFaultAtExit_IsHarmless_AnythingElseIsNot()
    {
        const string exitStack = "   at __std_type_info_destroy_list(__type_info_node*)\n   at __scrt_uninitialize_type_info()\n   at <CrtImplementationDetails>.ModuleUninitializer.SingletonDomainUnload(Object source, EventArgs arguments)";

        Assert.True(CrashReport.IsHarmlessExitFault(new WithStack("Dll was not found.", exitStack)));
        Assert.False(CrashReport.IsHarmlessExitFault(new WithStack("Dll was not found.", "   at TrayTrigger.Services.GpuTelemetryService..ctor()")));
        Assert.False(CrashReport.IsHarmlessExitFault(new InvalidOperationException(exitStack)));
        Assert.False(CrashReport.IsHarmlessExitFault(null));
    }

    [Fact]
    public void WhenNothingCouldBeSaved_TheMessageSaysThat_AndGivesNoPath()
    {
        string message = CrashReport.Message(atStart: true, null);

        Assert.Contains("could not be saved", message);
        Assert.DoesNotContain("send that file", message);
    }
}
