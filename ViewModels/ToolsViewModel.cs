using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.Views;

namespace TrayTrigger.ViewModels;

/// <summary>
/// The Tools page: saved program shortcuts with tools-only categories, favorites, search, sort and
/// three views. Follows the Library's look and workflow where it applies, and none of its game
/// features (no sessions, profiles, scripts, playtime or metadata). See docs/apps-section-plan.md.
/// </summary>
public sealed class ToolsViewModel : ViewModelBase
{
    private readonly StorageService _storage;
    private readonly IconExtractorService _icons;
    private readonly ShortcutService _shortcuts;
    private readonly ToolLauncherService _launcher;
    private readonly XboxScannerService _xboxScanner;
    private readonly AppSettings _settings;

    /// <summary>Tools waiting in the launch delay or at an admin prompt: a second press is ignored until they finish.</summary>
    private readonly HashSet<string> _launchesInFlight = new(StringComparer.Ordinal);

    private string _selectedCategory;
    private string _searchText = string.Empty;
    private string _statusMessage = string.Empty;

    public ToolsViewModel(StorageService storage, IconExtractorService icons, ShortcutService shortcuts, ToolLauncherService launcher, XboxScannerService xboxScanner, AppSettings settings)
    {
        _storage = storage;
        _icons = icons;
        _shortcuts = shortcuts;
        _launcher = launcher;
        _xboxScanner = xboxScanner;
        _settings = settings;
        _selectedCategory = string.IsNullOrWhiteSpace(settings.LastToolsCategoryTab) ? LibraryConstants.AllCategory : settings.LastToolsCategoryTab;

        FilteredTools = new ListCollectionView(Tools)
        {
            Filter = item => item is ToolCardViewModel card
                && ToolCatalog.IsInTab(card.Tool, SelectedCategory)
                && ToolCatalog.MatchesSearch(card.Tool, SearchText),
            CustomSort = new CardComparer(this)
        };

        SetViewModeCommand = new RelayCommand(mode => ViewMode = mode?.ToString() ?? ToolCatalog.ViewLargeIcons);
        AddToolCommand = new RelayCommand(AddToolFromPicker);
        OpenAppsFolderCommand = new RelayCommand(OpenAppsFolder);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
    }

    // ------------------------------------------------------------------ collections & state

    public ObservableCollection<ToolCardViewModel> Tools { get; } = new();
    public ICollectionView FilteredTools { get; }
    public ObservableCollection<CategoryTabItem> CategoryTabs { get; } = new();

    public ICommand SetViewModeCommand { get; }
    public ICommand AddToolCommand { get; }
    public ICommand OpenAppsFolderCommand { get; }
    public ICommand ClearSearchCommand { get; }

    /// <summary>Set by App: the same launch popup games use.</summary>
    public ILaunchPopup? LaunchPopup { get; set; }
    /// <summary>Set by MainViewModel: the in-window "Launching..." notice games use.</summary>
    public Action<string>? ShowLaunchNotice { get; set; }
    public Action? HideLaunchNotice { get; set; }

    /// <summary>Tools were added, removed or edited: hotkeys and the tray menu need rebuilding.</summary>
    public event Action? ToolsChanged;

    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            string next = string.IsNullOrWhiteSpace(value) ? LibraryConstants.AllCategory : value;
            if (string.Equals(_selectedCategory, next, StringComparison.Ordinal)) return;
            _selectedCategory = next;
            _settings.LastToolsCategoryTab = next;
            SaveSettings();
            foreach (var tab in CategoryTabs)
            {
                tab.IsSelected = string.Equals(tab.Name, next, StringComparison.OrdinalIgnoreCase);
            }
            OnPropertyChanged();
            RefreshView();
            SelectionClearRequested?.Invoke();
        }
    }

    /// <summary>
    /// The selection should be dropped: the search or the category tab changed, so a selected tool
    /// may no longer be on screen and must not be changed or removed by a batch action unseen.
    /// The view owns the list's selection, so it does the clearing.
    /// </summary>
    public event Action? SelectionClearRequested;

    public string SearchText
    {
        get => _searchText;
        set
        {
            string next = value ?? string.Empty;
            if (_searchText == next) return;
            _searchText = next;
            OnPropertyChanged();
            RefreshView();
            SelectionClearRequested?.Invoke();
        }
    }

    public IReadOnlyList<string> SortOptions => ToolCatalog.SortOptions;

    public string SelectedSortOption
    {
        get => ToolCatalog.NormalizeSortOption(_settings.ToolsSortOption);
        set
        {
            string next = ToolCatalog.NormalizeSortOption(value);
            if (next == SelectedSortOption && _settings.ToolsSortOption == next) return;
            _settings.ToolsSortOption = next;
            SaveSettings();
            OnPropertyChanged();
            RefreshView();
        }
    }

    public string ViewMode
    {
        get => ToolCatalog.NormalizeViewMode(_settings.ToolsViewMode);
        set
        {
            string next = ToolCatalog.NormalizeViewMode(value);
            if (next == ViewMode && _settings.ToolsViewMode == next) return;
            _settings.ToolsViewMode = next;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLargeIconsView));
            OnPropertyChanged(nameof(IsSmallIconsView));
            OnPropertyChanged(nameof(IsListView));
            OnPropertyChanged(nameof(ShowListHeader));
        }
    }

    public bool IsLargeIconsView => ViewMode == ToolCatalog.ViewLargeIcons;
    public bool IsSmallIconsView => ViewMode == ToolCatalog.ViewSmallIcons;
    public bool IsListView => ViewMode == ToolCatalog.ViewList;

    public string StatusMessage
    {
        get => _statusMessage;
        set { if (SetProperty(ref _statusMessage, value ?? string.Empty)) LoggingService.Shown("Tools status", value); }
    }

    public bool HasAnyTools => Tools.Count > 0;
    public bool IsFilteredEmpty => FilteredTools.IsEmpty;
    public bool ShowEmptyLibrary => !HasAnyTools;
    public bool ShowFavoritesEmpty => HasAnyTools && IsFilteredEmpty && SearchText.Trim().Length == 0
        && string.Equals(SelectedCategory, LibraryConstants.FavoritesCategory, StringComparison.OrdinalIgnoreCase);
    public bool ShowNoMatches => HasAnyTools && IsFilteredEmpty && !ShowFavoritesEmpty;
    public bool ShowListHeader => IsListView && !IsFilteredEmpty;
    public string ToolCountDisplay => Tools.Count == 1 ? "1 tool" : $"{Tools.Count} tools";

    /// <summary>Every tool's launch hotkey, for MainViewModel's single registration pass.</summary>
    public IEnumerable<HotkeyBinding> HotkeyBindings => Tools.Select(c => ToolCatalog.HotkeyBindingFor(c.Tool));

    /// <summary>The tray's Tools submenu: every tool, in the tray's own sort order.</summary>
    public IReadOnlyList<ToolCardViewModel> TrayTools() =>
        ToolCatalog.Sort(Tools, c => c.Tool, _settings.ToolsTraySortOption).ToList();

    public ToolCardViewModel? FindCard(string id) => Tools.FirstOrDefault(c => c.Id == id);

    // ------------------------------------------------------------------ load & save

    public void Load()
    {
        Tools.Clear();
        foreach (var tool in _storage.LoadTools())
        {
            Tools.Add(new ToolCardViewModel(tool, this));
        }
        RebuildTabs();
        RefreshView();
        if (!string.IsNullOrWhiteSpace(_storage.ToolsLoadWarning))
        {
            StatusMessage = _storage.ToolsLoadWarning;
        }
    }

    /// <summary>Dev screenshots only (App.DevDiagnostics): shows the given tools without saving anything.</summary>
    internal void ReplaceToolsForCapture(IEnumerable<ToolEntry> tools)
    {
        Tools.Clear();
        foreach (var tool in tools)
        {
            Tools.Add(new ToolCardViewModel(tool, this));
        }
        RebuildTabs();
        RefreshView();
        OnPropertyChanged(string.Empty);
    }

    private void Save() => _storage.SaveTools(Tools.Select(c => c.Tool));

    private void SaveSettings() => _storage.SaveSettings(_settings, source: "ToolsViewModel");

    /// <summary>After any change to the tools: tabs, view, disk, then hotkeys and tray.</summary>
    private void CommitChanges(string? status = null)
    {
        RebuildTabs();
        RefreshView();
        Save();
        if (status != null) StatusMessage = status;
        ToolsChanged?.Invoke();
    }

    private void RebuildTabs()
    {
        var tabs = ToolCatalog.TabsFor(Tools.Select(c => c.Tool));
        string? kept = tabs.FirstOrDefault(t => string.Equals(t, _selectedCategory, StringComparison.OrdinalIgnoreCase));
        if (kept == null)
        {
            // The selected category's last tool left it: back to All, as the Library does.
            _selectedCategory = LibraryConstants.AllCategory;
            _settings.LastToolsCategoryTab = LibraryConstants.AllCategory;
        }
        else
        {
            _selectedCategory = kept;
        }

        CategoryTabs.Clear();
        foreach (string tab in tabs)
        {
            string display = tab == LibraryConstants.AllCategory ? "All Tools" : tab;
            bool isSelected = string.Equals(tab, _selectedCategory, StringComparison.OrdinalIgnoreCase);
            CategoryTabs.Add(new CategoryTabItem(tab, display, isSelected, name => SelectedCategory = name));
        }
        OnPropertyChanged(nameof(SelectedCategory));
    }

    private void RefreshView()
    {
        FilteredTools.Refresh();
        OnPropertyChanged(nameof(HasAnyTools));
        OnPropertyChanged(nameof(IsFilteredEmpty));
        OnPropertyChanged(nameof(ShowEmptyLibrary));
        OnPropertyChanged(nameof(ShowFavoritesEmpty));
        OnPropertyChanged(nameof(ShowNoMatches));
        OnPropertyChanged(nameof(ShowListHeader));
        OnPropertyChanged(nameof(ToolCountDisplay));
    }

    // ------------------------------------------------------------------ adding

    private void AddToolFromPicker()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add Tool",
            Filter = "Programs, scripts and shortcuts (*.exe;*.bat;*.cmd;*.ps1;*.lnk)|*.exe;*.bat;*.cmd;*.ps1;*.lnk",
            Multiselect = true,
            CheckFileExists = true
        };
        if (FileDialogCloak.Show(dialog) == true)
        {
            AddFiles(dialog.FileNames);
        }
    }

    /// <summary>
    /// Opens Windows' Applications folder (shell:AppsFolder), every app in the Start menu, so one can be
    /// dragged onto the page. The only way to add a Store app, which has no .exe for Add Tool to pick.
    /// </summary>
    private void OpenAppsFolder()
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(SystemExecutables.Explorer, "shell:AppsFolder") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Tools", $"Could not open the Applications folder: {ex.Message}");
            StatusMessage = "Couldn't open the Applications folder.";
        }
    }

    /// <summary>Files dropped on the Tools page (or anywhere on the window while it is showing).</summary>
    public void HandleDrop(string[] files) => AddFiles(files.Where(f => !string.IsNullOrWhiteSpace(f)));

    /// <summary>Apps dropped from shell:AppsFolder, which come as shell items with no file path.</summary>
    public void HandleShellDrop(IReadOnlyList<ShellApp> apps) => AddCandidates(apps.Select(ShellCandidate).OfType<ToolCandidate>());

    private void AddFiles(IEnumerable<string> paths) => AddCandidates(paths.Select(FileCandidate).OfType<ToolCandidate>());

    /// <summary>A tool about to be added (<see cref="Tool"/>), or why the dropped item can't be one (<see cref="Reason"/>).</summary>
    /// <param name="Label">How the item is named in the "Not Added" list and the log.</param>
    /// <param name="IconSource">Where the icon comes from: a file, or a Store app's <see cref="ToolCatalog.AppsFolderPath"/>. Not extracted until the tool is really added.</param>
    private sealed record ToolCandidate(string Label, ToolEntry? Tool, string? Reason, string IconSource = "");

    /// <summary>The candidate for a dropped or picked file. Null for a file that is gone, which is skipped silently.</summary>
    private ToolCandidate? FileCandidate(string path)
    {
        string label = Path.GetFileName(path);
        if (Directory.Exists(path)) return new ToolCandidate(label, null, "it's a folder; drop the program or its shortcut instead");
        if (!File.Exists(path)) return null;

        var tool = TryCreateTool(path, out string? reason, out string iconSource);
        return new ToolCandidate(label, tool, reason, iconSource);
    }

    private ToolCandidate? ShellCandidate(ShellApp app)
    {
        if (app.FilePath != null) return FileCandidate(app.FilePath);

        string label = app.Name.Length > 0 ? app.Name : "Unnamed app";
        if (app.AppId != null)
        {
            if (XboxGameReason(app.AppId) is { } gameReason) return new ToolCandidate(label, null, gameReason);
            return new ToolCandidate(label, NewStoreAppTool(label, app.AppId), null, ToolCatalog.AppsFolderPath(app.AppId));
        }
        if (app.ProgramPath != null)
        {
            // The same rules as a dropped .exe, named the way Windows shows the app rather than after its file.
            var candidate = FileCandidate(app.ProgramPath) ?? new ToolCandidate(label, null, "its file doesn't exist");
            if (candidate.Tool != null) candidate.Tool.Name = label;
            return candidate with { Label = label };
        }
        return new ToolCandidate(label, null, "it isn't a program or a Store app");
    }

    private string? XboxGameReason(string appId) => ToolCatalog.GameReason(appId, id => _xboxScanner.FindByAumid(id) != null);

    /// <summary>A Game Pass or Store game's own .exe (under its install or package folder) is refused like the game itself.</summary>
    private string? XboxGameProgramReason(string target) =>
        string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase)
            ? ToolCatalog.GameReason(target, _xboxScanner.ScanInstalledGames([]).SelectMany(g => new[] { g.InstallDir, g.PackageRoot }))
            : null;

    private ToolEntry NewStoreAppTool(string name, string appId, string arguments = "") => new()
    {
        Name = name,
        AppId = appId.Trim(),
        Arguments = arguments,
        Category = ToolCatalog.CategoryForNewTool(SelectedCategory)
    };

    private void AddCandidates(IEnumerable<ToolCandidate> candidates)
    {
        var added = new List<ToolEntry>();
        var skipped = new List<string>();
        Window? owner = WindowHelper.ActiveOwner();

        foreach (var candidate in candidates)
        {
            if (candidate.Tool is not { } tool)
            {
                skipped.Add($"{candidate.Label} - {candidate.Reason}");
                LoggingService.Warn("Tools", $"Skipped '{candidate.Label}': {candidate.Reason}.");
                continue;
            }
            string iconSource = candidate.IconSource;

            // The same program with other arguments is a different tool (cmd.exe running another script).
            var duplicate = Tools.FirstOrDefault(c => ToolCatalog.IsSameLaunch(c.Tool, tool));
            if (duplicate != null && !ModernDialog.Confirm(
                    owner,
                    "Tool Already Added",
                    $"\"{duplicate.Name}\" is already in Tools.",
                    "Add it again anyway?",
                    confirmText: "Add Anyway",
                    cancelText: "Skip"))
            {
                continue;
            }

            // Only once it is really being added, so a skipped duplicate leaves no icon file behind.
            tool.IconPath = _icons.ExtractAndCacheIcon(tool.Id, iconSource, tool.Name);
            Tools.Add(new ToolCardViewModel(tool, this));
            added.Add(tool);
            LoggingService.Info("Tools", $"Added tool '{tool.Name}' ('{ToolCatalog.LaunchDisplay(tool)}', category '{tool.Category}', run as admin {tool.RunAsAdmin}).");
        }

        if (added.Count > 0)
        {
            CommitChanges(added.Count == 1 ? $"Added \"{added[0].Name}\"" : $"Added {added.Count} tools");
        }

        if (skipped.Count > 0)
        {
            ModernDialog.ShowWarning(
                owner,
                "Not Added",
                skipped.Count == 1 ? "This wasn't added to Tools:" : "These weren't added to Tools:",
                string.Join("\n", skipped));
        }
    }

    /// <summary>The tool for a dropped or picked file, or null with <paramref name="reason"/>. <paramref name="iconSource"/> is where its icon comes from; it isn't extracted here.</summary>
    private ToolEntry? TryCreateTool(string path, out string? reason, out string iconSource)
    {
        string ext = Path.GetExtension(path);
        string target;
        string arguments = string.Empty;
        string workingDirectory;
        bool runAsAdmin = false;
        iconSource = string.Empty;

        if (string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var shortcut = _shortcuts.Resolve(path);
            reason = ToolCatalog.GameReason(shortcut);
            if (reason != null) return null;
            target = shortcut.TargetPath;
            arguments = shortcut.Arguments;
            workingDirectory = shortcut.WorkingDirectory;
            runAsAdmin = shortcut.RunAsAdmin;

            // No file target: a shortcut to a shell item, made by dragging an app out of shell:AppsFolder.
            if (string.IsNullOrWhiteSpace(target) && _shortcuts.ResolveShellItemTarget(path) is { } shellTarget)
            {
                if (shellTarget.AppId != null)
                {
                    reason = XboxGameReason(shellTarget.AppId);
                    if (reason != null) return null;
                    iconSource = ToolCatalog.AppsFolderPath(shellTarget.AppId);
                    return NewStoreAppTool(ToolCatalog.NameFromFile(path), shellTarget.AppId, arguments);
                }
                if (shellTarget.ProgramPath != null)
                {
                    target = shellTarget.ProgramPath;
                    if (string.IsNullOrWhiteSpace(workingDirectory)) workingDirectory = Path.GetDirectoryName(target) ?? string.Empty;
                }
            }

            // A shortcut's own .ico wins; anything else (an index into a DLL) falls back to the program.
            iconSource = string.Equals(Path.GetExtension(shortcut.IconLocation), ".ico", StringComparison.OrdinalIgnoreCase) && File.Exists(shortcut.IconLocation)
                ? shortcut.IconLocation
                : target;
        }
        else if (string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase) || ToolCatalog.IsScriptPath(path))
        {
            target = path;
            workingDirectory = Path.GetDirectoryName(path) ?? string.Empty;
            iconSource = path;
        }
        else
        {
            // A Steam game's Start menu or desktop entry is a .url: say where it belongs instead.
            reason = string.Equals(ext, ".url", StringComparison.OrdinalIgnoreCase) ? ToolCatalog.GameReason(_shortcuts.Resolve(path)) : null;
            reason ??= "it isn't a program (.exe) or a shortcut (.lnk)";
            return null;
        }

        reason = ToolCatalog.ValidateTarget(target) ?? XboxGameProgramReason(target);
        if (reason != null) return null;

        return new ToolEntry
        {
            Name = ToolCatalog.NameFromFile(path),
            TargetPath = target,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RunAsAdmin = runAsAdmin,
            Category = ToolCatalog.CategoryForNewTool(SelectedCategory)
        };
    }

    // ------------------------------------------------------------------ launching

    public void Launch(ToolCardViewModel card)
    {
        if (!_launchesInFlight.Add(card.Id))
        {
            LoggingService.Info("Tools", $"'{card.Name}' is already being launched; not launching again.");
            return;
        }

        var target = new LaunchTarget(card.Id, card.Name);
        card.RefreshState();
        if (card.IsMissing)
        {
            _launchesInFlight.Remove(card.Id);
            ShowMissing(card, target);
            return;
        }

        // Same as a game: the notice first, then the launch after the shared delay.
        if (LaunchPopup?.TryBeginLaunch(target) != true)
        {
            ShowLaunchNotice?.Invoke($"Launching \"{card.Name}\"...");
        }

        var timer = new DispatcherTimer { Interval = LibraryViewModel.LaunchDispatchDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!Tools.Contains(card) || !_settings.EnableTools)
            {
                // Removed, or Tools turned off, during the delay: there's nothing to start any more.
                _launchesInFlight.Remove(card.Id);
                HideLaunchNotice?.Invoke();
                LaunchPopup?.LaunchDispatched(card.Id);
                LoggingService.Info("Tools", $"'{card.Name}' was removed or Tools was turned off before it launched; not launching.");
                return;
            }
            _ = DispatchAsync(card, target);
        };
        timer.Start();
    }

    private async Task DispatchAsync(ToolCardViewModel card, LaunchTarget target)
    {
        ToolLaunchResult result;
        try
        {
            // Off the UI thread: an admin prompt holds Process.Start until it is answered.
            result = await Task.Run(() => _launcher.Launch(card.Tool));
        }
        catch (Exception ex)
        {
            LoggingService.Error("Tools", $"Launch of tool '{card.Name}' threw: {ex.Message}", ex);
            result = new ToolLaunchResult(ToolLaunchOutcome.Failed, ex.Message);
        }
        finally
        {
            _launchesInFlight.Remove(card.Id);
        }
        OnLaunched(card, target, result);
    }

    private void OnLaunched(ToolCardViewModel card, LaunchTarget target, ToolLaunchResult result)
    {
        switch (result.Outcome)
        {
            case ToolLaunchOutcome.Started:
            case ToolLaunchOutcome.ActivatedRunning:
                StatusMessage = $"Launched {card.Name}";
                LaunchPopup?.LaunchDispatched(card.Id);
                break;

            case ToolLaunchOutcome.Cancelled:
                // The admin prompt was declined: the user's own choice, so nothing is shown.
                HideLaunchNotice?.Invoke();
                LaunchPopup?.LaunchDispatched(card.Id);
                break;

            case ToolLaunchOutcome.AlreadyRunningNoWindow:
            {
                HideLaunchNotice?.Invoke();
                // A script with no window to show is a hidden one (or one in Windows Terminal), not a tray program.
                string message = card.IsScript ? $"\"{card.Name}\" is still running." : $"\"{card.Name}\" is already running.";
                string notice = card.IsScript
                    ? "It hasn't finished yet, so it wasn't started again."
                    : "It's already running and has no window to bring forward. Look for its icon in the system tray.";
                StatusMessage = message;
                LaunchPopup?.LaunchDispatched(card.Id);
                if (LaunchPopup?.TryShowNotice(target, notice) != true)
                {
                    ShowLaunchNotice?.Invoke(message);
                }
                break;
            }

            case ToolLaunchOutcome.Missing:
                HideLaunchNotice?.Invoke();
                card.RefreshState();
                ShowMissing(card, target);
                break;

            default:
            {
                HideLaunchNotice?.Invoke();
                string message = result.Error ?? "The tool couldn't be started.";
                StatusMessage = $"Error: {message}";
                if (LaunchPopup?.TryShowFailure(target, message, "Open TrayTrigger", action: null) != true)
                {
                    ModernDialog.ShowWarning(WindowHelper.ActiveOwner(), "Launch Error", message);
                }
                break;
            }
        }
    }

    private void ShowMissing(ToolCardViewModel card, LaunchTarget target)
    {
        if (card.IsStoreApp)
        {
            // Nothing to locate: a Store app lives where Windows installs it, or not at all.
            if (LaunchPopup?.TryShowFailure(target, "Its Store app isn't installed any more.", "Open TrayTrigger", action: null) != true)
            {
                ModernDialog.ShowWarning(
                    WindowHelper.ActiveOwner(),
                    "Store App Not Installed",
                    $"\"{card.Name}\" isn't installed for this Windows user any more.",
                    "Install it again from the Microsoft Store, or remove it from Tools.");
            }
            return;
        }

        if (LaunchPopup?.TryShowFailure(target, "Its program wasn't found.", "Locate program...", () => Locate(card)) != true)
        {
            PromptLocate(card);
        }
    }

    private void PromptLocate(ToolCardViewModel card)
    {
        bool locate = ModernDialog.Confirm(
            WindowHelper.ActiveOwner(),
            "Tool Program Missing",
            $"The program for \"{card.Name}\" was not found.",
            $"Expected location:\n{card.TargetPath}\n\nWould you like to locate it now?",
            confirmText: "Locate...",
            cancelText: "Cancel");
        if (locate) Locate(card);
    }

    public void Locate(ToolCardViewModel card)
    {
        if (card.IsStoreApp) return;
        var dialog = new OpenFileDialog
        {
            Title = $"Locate {(card.IsScript ? "Script" : "Program")} for {card.Name}",
            Filter = "Programs and scripts (*.exe;*.bat;*.cmd;*.ps1)|*.exe;*.bat;*.cmd;*.ps1",
            CheckFileExists = true
        };
        if (FileDialogCloak.Show(dialog) != true) return;

        string? problem = ToolCatalog.ValidateTarget(dialog.FileName);
        if (problem != null)
        {
            ModernDialog.ShowWarning(WindowHelper.ActiveOwner(), "Can't Use This File", $"This file can't be used because {problem}.");
            return;
        }

        card.Tool.WorkingDirectory = ToolCatalog.WorkingDirectoryAfterRetarget(card.Tool.WorkingDirectory, card.Tool.TargetPath, dialog.FileName);
        card.Tool.TargetPath = dialog.FileName;
        card.RefreshState();
        CommitChanges($"Updated the program for \"{card.Name}\"");
    }

    /// <summary>A tool's hotkey was pressed. Raised on the hotkey window's thread.</summary>
    public void OnHotkeyTriggered(string toolId)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var card = FindCard(toolId);
            if (card != null)
            {
                LoggingService.Info("Tools", $"Hotkey triggered launch for tool '{card.Name}'.");
                Launch(card);
            }
            else
            {
                LoggingService.Warn("Tools", $"Hotkey fired for unknown tool id '{toolId}'.");
            }
        });
    }

    // ------------------------------------------------------------------ editing

    public void ToggleFavorite(ToolCardViewModel card)
    {
        card.Tool.IsFavorite = !card.Tool.IsFavorite;
        card.RefreshState();
        CommitChanges();
    }

    public void Edit(ToolCardViewModel card)
    {
        var dialog = new ToolEditDialog(card.Tool, ToolCatalog.CategoriesOf(Tools.Select(c => c.Tool)), _icons);
        if (dialog.ShowDialog() == true)
        {
            card.Refresh();
            CommitChanges($"Saved \"{card.Name}\"");
        }
    }

    public void Rename(ToolCardViewModel card)
    {
        var dialog = new QuickInputDialog("Rename Tool", "Rename Tool", $"Enter a new name for \"{card.Name}\":", card.Name)
        {
            Owner = WindowHelper.ActiveOwner()
        };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResultValue))
        {
            card.Tool.Name = dialog.ResultValue.Trim();
            card.RefreshState();
            CommitChanges($"Renamed to \"{card.Name}\"");
        }
    }

    public void ChangeCategory(ToolCardViewModel card)
    {
        var dialog = new QuickInputDialog(
            "Change Category",
            "Change Category",
            $"Choose or type a category for \"{card.Name}\":",
            card.Category,
            ToolCatalog.CategoriesOf(Tools.Select(c => c.Tool)))
        {
            Owner = WindowHelper.ActiveOwner()
        };
        if (dialog.ShowDialog() == true)
        {
            card.Tool.Category = LibraryConstants.NormalizeCategory(dialog.ResultValue);
            card.RefreshState();
            CommitChanges($"Moved \"{card.Name}\" to {card.Category}");
        }
    }

    public void ChangeIcon(ToolCardViewModel card)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Select Icon for \"{card.Name}\"",
            Filter = "Image & Icon Files (*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.exe)|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.exe|All Files (*.*)|*.*",
            CheckFileExists = true
        };
        if (FileDialogCloak.Show(dialog) != true) return;

        string cached = _icons.ExtractAndCacheIcon(card.Id, dialog.FileName, card.Name);
        if (!string.IsNullOrEmpty(cached))
        {
            card.Tool.IconPath = cached;
            card.Refresh();
            CommitChanges($"Updated icon for \"{card.Name}\"");
        }
    }

    public void OpenFolder(ToolCardViewModel card)
    {
        if (card.IsStoreApp) return;
        try
        {
            if (File.Exists(card.TargetPath))
            {
                Process.Start(new ProcessStartInfo(SystemExecutables.Explorer, $"/select,\"{card.TargetPath}\"") { UseShellExecute = true });
                return;
            }
            string? folder = Path.GetDirectoryName(card.TargetPath);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo(SystemExecutables.Explorer, $"\"{folder}\"") { UseShellExecute = true });
                return;
            }
            StatusMessage = $"The folder for \"{card.Name}\" no longer exists.";
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Tools", $"Could not open the folder for '{card.Name}': {ex.Message}");
        }
    }

    public void Remove(ToolCardViewModel card)
    {
        bool confirmed = ModernDialog.ConfirmDelete(
            WindowHelper.ActiveOwner(),
            "Remove Tool",
            $"Remove \"{card.Name}\" from Tools?",
            "Only the shortcut in TrayTrigger is removed. The program itself isn't touched.");
        if (!confirmed) return;

        Tools.Remove(card);
        DeleteCachedIcon(card.Tool);
        LoggingService.Info("Tools", $"Removed tool '{card.Name}'.");
        CommitChanges($"Removed \"{card.Name}\"");
    }

    /// <summary>Deletes the tool's own cached icon: only Icons\{Id}.png, never a file another entry could use.</summary>
    private void DeleteCachedIcon(ToolEntry tool)
    {
        try
        {
            string own = Path.Combine(_storage.IconsDirectory, $"{tool.Id}.png");
            if (File.Exists(own)) File.Delete(own);
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("Tools", $"Could not delete the cached icon for '{tool.Name}': {ex.Message}");
        }
    }

    public void ToggleRunAsAdmin(ToolCardViewModel card)
    {
        if (card.IsStoreApp) return;
        card.Tool.RunAsAdmin = !card.Tool.RunAsAdmin;
        card.RefreshState();
        CommitChanges(card.RunAsAdmin ? $"\"{card.Name}\" now runs as administrator" : $"\"{card.Name}\" no longer runs as administrator");
    }

    // ------------------------------------------------------------------ selection & batch editing

    private List<ToolCardViewModel> _selectedTools = new();
    private ICommand? _batchFavoriteCommand;
    private ICommand? _batchChangeCategoryCommand;
    private ICommand? _batchRunAsAdminCommand;
    private ICommand? _batchRemoveCommand;

    /// <summary>Every selected tool, in the list's selection order. Set by the view (Ctrl+click, Shift+click, Ctrl+A).</summary>
    public IReadOnlyList<ToolCardViewModel> SelectedTools => _selectedTools;
    public bool HasMultipleSelection => _selectedTools.Count > 1;
    public string SelectionSummary => $"{_selectedTools.Count} tools selected";
    public string BatchFavoriteLabel => ToolCatalog.ShouldFavoriteAll(_selectedTools.Select(c => c.Tool)) ? "Add to Favorites" : "Remove from Favorites";
    /// <summary>Store apps can't run as administrator, so the batch item only looks at programs and is off when none is selected.</summary>
    public bool BatchCanRunAsAdmin => _selectedTools.Any(c => !c.IsStoreApp);
    public bool BatchAllRunAsAdmin => BatchCanRunAsAdmin && _selectedTools.Where(c => !c.IsStoreApp).All(c => c.RunAsAdmin);

    public ICommand BatchFavoriteCommand => _batchFavoriteCommand ??= new RelayCommand(BatchFavorite);
    public ICommand BatchChangeCategoryCommand => _batchChangeCategoryCommand ??= new RelayCommand(BatchChangeCategory);
    public ICommand BatchRunAsAdminCommand => _batchRunAsAdminCommand ??= new RelayCommand(BatchRunAsAdmin);
    public ICommand BatchRemoveCommand => _batchRemoveCommand ??= new RelayCommand(BatchRemove);

    public void SetSelection(IEnumerable<ToolCardViewModel> cards)
    {
        _selectedTools = cards.ToList();
        NotifySelection();
    }

    private void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectedTools));
        OnPropertyChanged(nameof(HasMultipleSelection));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(BatchFavoriteLabel));
        OnPropertyChanged(nameof(BatchCanRunAsAdmin));
        OnPropertyChanged(nameof(BatchAllRunAsAdmin));
    }

    private void BatchFavorite()
    {
        var cards = _selectedTools.ToList();
        if (cards.Count == 0) return;
        bool favorite = ToolCatalog.ShouldFavoriteAll(cards.Select(c => c.Tool));
        foreach (var card in cards)
        {
            card.Tool.IsFavorite = favorite;
            card.RefreshState();
        }
        CommitChanges(favorite ? $"Added {cards.Count} tools to Favorites" : $"Removed {cards.Count} tools from Favorites");
        NotifySelection();
    }

    private void BatchChangeCategory()
    {
        var cards = _selectedTools.ToList();
        if (cards.Count == 0) return;
        var dialog = new QuickInputDialog(
            "Change Category",
            "Change Category",
            $"Choose or type a category for {cards.Count} tools:",
            ToolCatalog.CommonCategory(cards.Select(c => c.Tool)),
            ToolCatalog.CategoriesOf(Tools.Select(c => c.Tool)))
        {
            Owner = WindowHelper.ActiveOwner()
        };
        if (dialog.ShowDialog() != true) return;

        string category = LibraryConstants.NormalizeCategory(dialog.ResultValue);
        foreach (var card in cards)
        {
            card.Tool.Category = category;
            card.RefreshState();
        }
        CommitChanges($"Moved {cards.Count} tools to {category}");
        NotifySelection();
    }

    private void BatchRunAsAdmin()
    {
        var cards = _selectedTools.Where(c => !c.IsStoreApp).ToList();
        if (cards.Count == 0) return;
        bool runAsAdmin = ToolCatalog.ShouldRunAllAsAdmin(cards.Select(c => c.Tool));
        foreach (var card in cards)
        {
            card.Tool.RunAsAdmin = runAsAdmin;
            card.RefreshState();
        }
        CommitChanges(runAsAdmin ? $"{cards.Count} tools now run as administrator" : $"{cards.Count} tools no longer run as administrator");
        NotifySelection();
    }

    private void BatchRemove()
    {
        var cards = _selectedTools.ToList();
        if (cards.Count == 0) return;
        if (cards.Count == 1)
        {
            Remove(cards[0]);
            return;
        }

        bool confirmed = ModernDialog.ConfirmDelete(
            WindowHelper.ActiveOwner(),
            "Remove Tools",
            $"Remove {cards.Count} tools from Tools?",
            "Only the shortcuts in TrayTrigger are removed. The programs themselves aren't touched.");
        if (!confirmed) return;

        foreach (var card in cards)
        {
            Tools.Remove(card);
            DeleteCachedIcon(card.Tool);
            LoggingService.Info("Tools", $"Removed tool '{card.Name}'.");
        }
        _selectedTools.Clear();
        CommitChanges($"Removed {cards.Count} tools");
        NotifySelection();
    }

    private sealed class CardComparer(ToolsViewModel owner) : IComparer
    {
        public int Compare(object? x, object? y) =>
            x is ToolCardViewModel a && y is ToolCardViewModel b
                ? ToolCatalog.Compare(a.Tool, b.Tool, owner.SelectedSortOption)
                : 0;
    }
}
