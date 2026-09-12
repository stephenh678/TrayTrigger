using System.IO;
using System.Runtime.ExceptionServices;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// Commit batching in <see cref="ImportCoordinator"/>.
///
/// One "Scan for Games" confirmation runs up to seven imports back to back - Steam, GOG, EA,
/// Epic, Ubisoft, Xbox, folder - and each used to finish by rewriting games.json and settings.json
/// and tearing down and re-registering every hotkey. A nine-game scan on a real machine wrote the
/// whole library six times over and left six windows in which the global hotkey was unbound.
/// <see cref="ImportCoordinator.BeginCommitBatch"/> coalesces that tail into one.
///
/// Built against a <see cref="StorageService"/> scoped to a temp folder so nothing touches the
/// user's library, and on an STA thread because the library view model owns WPF collection views
/// and the hotkey listener's message-only window.
/// </summary>
public class ImportCommitBatchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
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

    private (ImportCoordinator Import, StorageService Storage) CreateCoordinator()
    {
        var storage = new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"));
        var icons = new IconExtractorService(storage);
        var steamScanner = new SteamScannerService();
        var gogScanner = new GogScannerService();
        var eaScanner = new EaScannerService();
        var epicScanner = new EpicScannerService();
        var ubisoftScanner = new UbisoftScannerService();
        var xboxScanner = new XboxScannerService();
        var steamSearch = new SteamSearchService();
        var steamMetadata = new SteamMetadataService();
        var settings = new AppSettings();

        var launcher = new ProcessLauncherService(
            storage, new PerformanceProfileService(storage), new GameScriptService(),
            steamScanner, gogScanner, eaScanner, epicScanner, ubisoftScanner, xboxScanner);

        var library = new LibraryViewModel(
            storage, icons, launcher, new HotkeyManager(), steamMetadata, steamSearch, settings,
            getUseVerticalPosterArt: () => true,
            getSteamGridDbApiKeyOrNull: () => null);

        var import = new ImportCoordinator(
            library, new ShortcutService(), icons, new FolderScannerService(),
            steamScanner, gogScanner, eaScanner, epicScanner, ubisoftScanner, xboxScanner,
            steamSearch, steamMetadata, storage, settings,
            getSteamGridDbApiKeyOrNull: () => null);

        return (import, storage);
    }

    private GameEntry Game(string name) => new()
    {
        Name = name,
        Category = "Action",
        ExecutablePath = Path.Combine(_root, "games", name, "game.exe"),
    };

    /// <summary>Baseline: outside a batch every commit still persists immediately, so a one-off
    /// import (drag-and-drop, "Add Game") is not left unsaved.</summary>
    [Fact]
    public void WithoutBatch_EachCommitWritesTheLibrary() => Sta(() =>
    {
        var (import, storage) = CreateCoordinator();
        int before = storage.GamesSaveCount;

        import.CommitImportedEntries([Game("Alpha")], null);
        import.CommitImportedEntries([Game("Bravo")], null);
        import.CommitImportedEntries([Game("Charlie")], null);

        Assert.Equal(before + 3, storage.GamesSaveCount);
    });

    /// <summary>The fix: the same three commits inside one batch write once, at the end.</summary>
    [Fact]
    public void WithBatch_ManyCommitsWriteOnce() => Sta(() =>
    {
        var (import, storage) = CreateCoordinator();
        int before = storage.GamesSaveCount;

        using (import.BeginCommitBatch())
        {
            import.CommitImportedEntries([Game("Alpha")], null);
            import.CommitImportedEntries([Game("Bravo")], null);
            import.CommitImportedEntries([Game("Charlie")], null);

            // Nothing on disk yet - the whole point.
            Assert.Equal(before, storage.GamesSaveCount);
        }

        Assert.Equal(before + 1, storage.GamesSaveCount);
    });

    /// <summary>Every game still reaches the library; batching defers the write, not the import.</summary>
    [Fact]
    public void WithBatch_AllEntriesStillLandInTheLibraryAndOnDisk() => Sta(() =>
    {
        var (import, storage) = CreateCoordinator();

        using (import.BeginCommitBatch())
        {
            import.CommitImportedEntries([Game("Alpha"), Game("Bravo")], null);
            import.CommitImportedEntries([Game("Charlie")], null);
        }

        var reloaded = storage.LoadGames();
        Assert.Equal(3, reloaded.Count);
        Assert.Contains(reloaded, g => g.Name == "Alpha");
        Assert.Contains(reloaded, g => g.Name == "Bravo");
        Assert.Contains(reloaded, g => g.Name == "Charlie");
    });

    /// <summary>
    /// A batch that never commits anything must not write. Relevant because a scan whose every
    /// result was a duplicate runs all seven legs and adds nothing.
    /// </summary>
    [Fact]
    public void EmptyBatch_WritesNothing() => Sta(() =>
    {
        var (import, storage) = CreateCoordinator();
        int before = storage.GamesSaveCount;

        using (import.BeginCommitBatch()) { }

        Assert.Equal(before, storage.GamesSaveCount);
    });

    /// <summary>Only the outermost scope flushes, so nesting cannot reintroduce per-leg writes.</summary>
    [Fact]
    public void NestedBatches_FlushOnlyOnce() => Sta(() =>
    {
        var (import, storage) = CreateCoordinator();
        int before = storage.GamesSaveCount;

        using (import.BeginCommitBatch())
        {
            using (import.BeginCommitBatch())
            {
                import.CommitImportedEntries([Game("Alpha")], null);
                import.CommitImportedEntries([Game("Bravo")], null);
            }

            Assert.Equal(before, storage.GamesSaveCount);
            import.CommitImportedEntries([Game("Charlie")], null);
        }

        Assert.Equal(before + 1, storage.GamesSaveCount);
    });

    /// <summary>
    /// A leg that throws still unwinds the batch, and what was already committed is still saved.
    /// Without this a failed import would leave the coordinator batching forever - every later
    /// import silently unsaved until restart.
    /// </summary>
    [Fact]
    public void BatchDisposedAfterFailure_StillFlushesAndResets() => Sta(() =>
    {
        var (import, storage) = CreateCoordinator();
        int before = storage.GamesSaveCount;

        // Cast: a lambda whose body only throws is convertible to Func<Task> too, and xunit's
        // async overload wins the ambiguity.
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using (import.BeginCommitBatch())
            {
                import.CommitImportedEntries([Game("Alpha")], null);
                throw new InvalidOperationException("import leg blew up");
            }
        }));

        Assert.Equal(before + 1, storage.GamesSaveCount);

        // And the coordinator is back to writing immediately.
        import.CommitImportedEntries([Game("Bravo")], null);
        Assert.Equal(before + 2, storage.GamesSaveCount);
    });
}
