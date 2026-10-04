using System.IO;
using System.Net;
using System.Runtime.ExceptionServices;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// The SteamGridDB and RAWG key boxes in Settings and the Welcome: what a check reports, and what
/// pasting a key does. The HTTP calls themselves read the live services and are exercised by hand.
/// One class, because <see cref="ApiKeyCheckViewModel.AutoCheckDelay"/> is static and xUnit runs
/// the tests of a class one at a time.
/// </summary>
public class ApiKeyCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"));
    private readonly TimeSpan _savedDelay = ApiKeyCheckViewModel.AutoCheckDelay;

    public void Dispose()
    {
        ApiKeyCheckViewModel.AutoCheckDelay = _savedDelay;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void Sta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>A settings view model whose key edits never reach the real services: the
    /// automatic check waits longer than any test runs.</summary>
    private SettingsViewModel NewSettings(AppSettings settings)
    {
        ApiKeyCheckViewModel.AutoCheckDelay = TimeSpan.FromHours(1);
        var storage = new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"));
        return new SettingsViewModel(settings, storage, new StartupManager(), new TrayPromotionService(), new SteamScannerService());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, ApiKeyCheckResult.Valid)]
    [InlineData(HttpStatusCode.Unauthorized, ApiKeyCheckResult.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, ApiKeyCheckResult.Rejected)]
    // Says nothing about the key: it must not be reported as wrong.
    [InlineData(HttpStatusCode.TooManyRequests, ApiKeyCheckResult.Unreachable)]
    [InlineData(HttpStatusCode.InternalServerError, ApiKeyCheckResult.Unreachable)]
    [InlineData(HttpStatusCode.NotFound, ApiKeyCheckResult.Unreachable)]
    public void StatusCodes_MapToAVerdictOnTheKey(HttpStatusCode status, ApiKeyCheckResult expected)
    {
        Assert.Equal(expected, ApiKeyCheck.FromStatus(status));
    }

    [Theory]
    [InlineData(ApiKeyCheckResult.Valid, ApiKeyCheckState.Valid, true)]
    [InlineData(ApiKeyCheckResult.Rejected, ApiKeyCheckState.Rejected, false)]
    [InlineData(ApiKeyCheckResult.Unreachable, ApiKeyCheckState.Unreachable, false)]
    public async Task Check_ReportsTheResult_AndOnlyAWorkingKeyIsAccepted(ApiKeyCheckResult result, ApiKeyCheckState expected, bool accepted)
    {
        string? acceptedKey = null;
        var check = new ApiKeyCheckViewModel("SteamGridDB", () => "abc123", (_, _) => Task.FromResult(result));
        check.KeyAccepted += key => acceptedKey = key;

        await check.CheckNowAsync();

        Assert.Equal(expected, check.State);
        Assert.True(check.HasMessage);
        Assert.Equal(accepted ? "abc123" : null, acceptedKey);
    }

    [Fact]
    public async Task Check_WithNoKey_AsksNothing()
    {
        int calls = 0;
        var check = new ApiKeyCheckViewModel("RAWG", () => "  ", (_, _) => { calls++; return Task.FromResult(ApiKeyCheckResult.Valid); });

        await check.CheckNowAsync();

        Assert.Equal(0, calls);
        Assert.Equal(ApiKeyCheckState.None, check.State);
        Assert.False(check.HasMessage);
    }

    /// <summary>Typing a key by hand mustn't send a request per character: only the key as it
    /// stands when typing pauses is checked.</summary>
    [Fact]
    public async Task ScheduledChecks_WaitForTypingToPause()
    {
        ApiKeyCheckViewModel.AutoCheckDelay = TimeSpan.FromMilliseconds(150);
        string key = "a";
        var checkedKeys = new List<string>();
        var check = new ApiKeyCheckViewModel("RAWG", () => key, (k, _) => { lock (checkedKeys) checkedKeys.Add(k); return Task.FromResult(ApiKeyCheckResult.Valid); });

        check.ScheduleCheck();
        key = "ab";
        check.ScheduleCheck();
        key = "abc";
        check.ScheduleCheck();

        for (int i = 0; i < 50 && check.State != ApiKeyCheckState.Valid; i++)
            await Task.Delay(50);

        Assert.Equal(ApiKeyCheckState.Valid, check.State);
        lock (checkedKeys) Assert.Equal(["abc"], checkedKeys);
    }

    [Fact]
    public void PastingAKey_SwitchesItsSourceOn_AndDropsCopiedWhitespace() => Sta(() =>
    {
        var settings = new AppSettings();
        var vm = NewSettings(settings);

        vm.SteamGridDbApiKey = "  0123456789abcdef0123456789abcdef\r\n";
        vm.RawgApiKey = "\tfedcba9876543210fedcba9876543210 ";

        Assert.True(settings.UseSteamGridDbArt);
        Assert.True(settings.UseRawgMetadata);
        Assert.Equal("0123456789abcdef0123456789abcdef", settings.SteamGridDbApiKey);
        Assert.Equal("fedcba9876543210fedcba9876543210", settings.RawgApiKey);
        Assert.NotNull(vm.SteamGridDbApiKeyOrNull);
        Assert.NotNull(vm.RawgApiKeyOrNull);
    });

    /// <summary>Only a key going from none to one says "use it". Switching a source off and then
    /// correcting its key is left off, and clearing a key doesn't flip anything either.</summary>
    [Fact]
    public void EditingAnExistingKey_LeavesTheSwitchAlone() => Sta(() =>
    {
        var settings = new AppSettings { SteamGridDbApiKey = "oldkey", UseSteamGridDbArt = false };
        var vm = NewSettings(settings);

        vm.SteamGridDbApiKey = "newkey";
        Assert.False(settings.UseSteamGridDbArt);

        vm.UseSteamGridDbArt = true;
        vm.SteamGridDbApiKey = string.Empty;
        Assert.True(settings.UseSteamGridDbArt);
    });
}
