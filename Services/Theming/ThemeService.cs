using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace TrayTrigger.Services.Theming;

/// <summary>
/// Carries "the theme changed" to bindings that hold a color computed in code (a converter, a view model
/// property): bind to <see cref="Version"/> as well and the binding is evaluated again.
/// </summary>
public sealed class ThemeState : INotifyPropertyChanged
{
    public static ThemeState Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    private int _version;
    public int Version => _version;

    internal void Bump()
    {
        _version++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
    }
}

/// <summary>
/// Puts a theme on screen: builds the colors of the chosen theme, swaps them for the ones in
/// <c>Application.Resources</c>, and tells the title bars and anything that cached a color. Every view reads
/// its colors with {DynamicResource}, so the swap repaints the app without a restart.
/// See docs/theming-plan.md.
/// </summary>
public sealed partial class ThemeService
{
    public static ThemeService Instance { get; } = new();

    /// <summary>The setting that follows Windows: Light when Windows' apps are set to light, Dark otherwise.</summary>
    public const string AutoId = "Auto";

    /// <summary>Raised on the UI thread after a theme is on screen.</summary>
    public event EventHandler? ThemeChanged;

    /// <summary>Raised when the list of themes changed: a file was added, edited or removed in the Themes folder.</summary>
    public event EventHandler? ThemesChanged;

    private string _selectedId = ThemeCatalog.DarkId;
    private ThemeDefinition _active = ThemeCatalog.BuiltIn(ThemeCatalog.DarkId)!;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private readonly object _debounceLock = new();
    private bool _followingWindows;
    private bool _hasApplied;
    private bool _showBackground = true;
    private bool _windowShown;

    /// <summary>What the user chose: a theme id, or <see cref="AutoId"/>.</summary>
    public string SelectedId => _selectedId;

    /// <summary>The theme on screen now (for Auto, the one Auto picked).</summary>
    public string ActiveName => _active.Name;

    public bool IsDark => _active.IsDark;

    /// <summary>
    /// Whether the theme's background picture is shown. Off leaves the theme's plain background color; the
    /// setting is the user's, and holds across themes.
    /// </summary>
    public bool ShowBackground
    {
        get => _showBackground;
        set
        {
            if (_showBackground == value) return;
            _showBackground = value;
            if (_hasApplied) ApplyBackground(_active);
        }
    }

    /// <summary>
    /// Whether the main window is on screen. The theme's picture (up to about 15 MB decoded) is held only while it is:
    /// it is not decoded until the window first shows, let go when the window hides to the tray, and decoded again on show.
    /// </summary>
    public bool WindowShown
    {
        get => _windowShown;
        set
        {
            if (_windowShown == value) return;
            _windowShown = value;
            if (_hasApplied) ApplyBackground(_active);
        }
    }

    /// <summary>Why the theme's background picture is not shown, when it should be; null otherwise.</summary>
    public string? BackgroundProblem { get; private set; }

    /// <summary>Why the chosen theme is not the one on screen, or null when it is.</summary>
    public string? FallbackReason { get; private set; }

    /// <summary>Text pairs of the active theme that are hard to read, for Settings to show.</summary>
    public IReadOnlyList<string> ContrastWarnings { get; private set; } = Array.Empty<string>();

    /// <summary>What the chosen theme's file asked for that was skipped (a mistyped color name, a value that is not a color), one line each.</summary>
    public IReadOnlyList<string> ThemeWarnings { get; private set; } = Array.Empty<string>();

    /// <summary>Files in the Themes folder that could not be used, one line each.</summary>
    public IReadOnlyList<string> Problems { get; private set; } = Array.Empty<string>();

    // ---- What there is to choose from ---------------------------------------------------------------

    public IReadOnlyList<ThemeInfo> Available()
    {
        var list = new List<ThemeInfo> { new(AutoId, "Auto (follow Windows)", null, BuiltIn: true, IsDark: true) };
        // Dark, the default, comes first; the other built-in looks follow alphabetically, and so do the player's own below them.
        var builtIns = ThemeCatalog.BuiltIns;
        list.AddRange(builtIns.Where(t => t.Id == ThemeCatalog.DarkId).Select(t => new ThemeInfo(t.Id, t.Name, t.Author, true, t.IsDark)));
        list.AddRange(builtIns.Where(t => t.Id != ThemeCatalog.DarkId).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new ThemeInfo(t.Id, t.Name, t.Author, true, t.IsDark)));
        list.Add(new ThemeInfo(ThemeCatalog.HighContrastId, "High Contrast (Windows colors)", "Windows", true, ThemeCatalog.HighContrast().IsDark));
        var (user, problems) = ThemeCatalog.LoadUserThemes();
        Problems = problems;
        list.AddRange(user.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(t => new ThemeInfo(t.Id, t.Name, t.Author, false, t.IsDark)));
        return list;
    }

    // ---- Applying a theme ------------------------------------------------------------------------------------

    /// <summary>
    /// Puts the theme with this id on screen and remembers the choice. An id that names nothing, or a file
    /// that cannot be read, leaves Dark on screen and says why in <see cref="FallbackReason"/>.
    /// </summary>
    /// <returns>True when the chosen theme itself is on screen.</returns>
    public bool Apply(string? id)
    {
        id = string.IsNullOrWhiteSpace(id) ? ThemeCatalog.DarkId : id;
        if (string.Equals(id, AutoId, StringComparison.OrdinalIgnoreCase)) id = AutoId;
        _selectedId = id;
        FallbackReason = null;

        try
        {
            return ApplyCore(id);
        }
        catch (Exception ex)
        {
            // A theme must never stop the app starting: whatever is on screen stays, and Settings says why.
            FallbackReason = $"The theme could not be applied ({ex.Message}).";
            LoggingService.Error("Theme", $"Applying theme '{id}' failed: {ex.Message}", ex);
            return false;
        }
    }

    private bool ApplyCore(string id)
    {

        ThemeDefinition? theme;
        if (string.Equals(id, AutoId, StringComparison.OrdinalIgnoreCase))
        {
            theme = ThemeCatalog.BuiltIn(AutoThemeId());
            FollowWindows(true);
        }
        else
        {
            FollowWindows(false);
            theme = ThemeCatalog.Find(id);
            if (theme == null)
            {
                FallbackReason = $"The theme \"{id.Replace("user:", "")}\" was not found, so Dark is in use.";
                LoggingService.Warn("Theme", FallbackReason);
            }
        }

        bool applied = theme != null;
        Show(theme ?? ThemeCatalog.BuiltIn(ThemeCatalog.DarkId)!);
        WatchUserThemes();
        return applied;
    }

    private void Show(ThemeDefinition theme)
    {
        var palette = ThemeCatalog.Resolve(theme);
        _active = theme;
        ContrastWarnings = ThemeCatalog.ContrastProblems(palette);
        foreach (string w in ContrastWarnings) LoggingService.Info("Theme", $"{theme.Name}: {w}");

        ThemeWarnings = (theme.Warnings ?? Array.Empty<string>())
            .Concat(ThemeCatalog.UnknownColorNames(theme).Select(name => $"\"{name}\" is not a color name; skipped."))
            .ToList();
        foreach (string w in ThemeWarnings) LoggingService.Warn("Theme", $"{theme.Name}: {w}");

        var app = Application.Current;
        if (app != null)
        {
            ReplaceColors(app.Resources, Build(palette));
            ReplaceShapes(app.Resources, ThemeShape.Build(theme.Shape));
            AppTypography.ApplyForTheme(theme.TextSize);
            AppFonts.ApplyForTheme(theme.Font);
            WindowThemeService.UseDarkChrome = theme.IsDark;
            WindowThemeService.ReapplyChrome();
        }
        ApplyBackground(theme);
        _hasApplied = true;
        ThemeState.Instance.Bump();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
        LoggingService.Info("Theme", $"Theme: {theme.Name}{(_selectedId == AutoId ? " (Auto)" : "")}.");
    }

    /// <summary>Loads the theme's picture (if it has one and the setting allows it) and hands it to the window.</summary>
    private void ApplyBackground(ThemeDefinition theme)
    {
        BackgroundProblem = null;
        BitmapSource? image = null;
        double strength = 1;
        string position = BackgroundPosition.Center;

        var resolved = ThemeCatalog.ResolveBackground(theme);
        if (resolved != null && _showBackground && _windowShown)
        {
            image = ThemeBackgroundLoader.Load(resolved, out string? problem);
            BackgroundProblem = problem;
            strength = resolved.Strength;
            position = resolved.Position;
        }
        ThemeBackgroundState.Instance.Set(image, strength, position);
    }

    /// <summary>A copy of Colors.xaml with the theme's colors in place of the file's own.</summary>
    internal static ResourceDictionary Build(ThemePalette palette)
    {
        var dictionary = AppResources.LoadFresh("Colors.xaml");
        Fill(dictionary, palette);
        return dictionary;
    }

    /// <summary>Sets every color of the palette (and the tab strip fades that follow the card color) in a dictionary made by <see cref="Build"/>. The editor's preview calls it again as a color is dragged.</summary>
    internal static void Fill(ResourceDictionary dictionary, ThemePalette palette)
    {
        bool cardChanged = false;
        foreach (var (name, color) in palette)
        {
            // A color that is already in is left alone: each one put in makes the preview look its colors up again, and
            // the preview calls this on every step of a drag. Only a frozen brush is kept, so any thread can read every brush in use.
            if (dictionary["Brush" + name] is SolidColorBrush { IsFrozen: true } current && current.Color == color) continue;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            dictionary["Brush" + name] = brush;
            if (dictionary.Contains("Color" + name)) dictionary["Color" + name] = color;
            if (name == "DialogCard") cardChanged = true;
        }

        // The fade at the edge of a scrolling tab strip has to be the strip's own color.
        if (cardChanged && palette.TryGetValue("DialogCard", out var card))
        {
            dictionary["BrushTabStripFadeLeft"] = Fade(card, solidAt: 0.45, clearAt: 1.0, solidFirst: true);
            dictionary["BrushTabStripFadeRight"] = Fade(card, solidAt: 0.55, clearAt: 0.0, solidFirst: false);
        }
    }

    private static LinearGradientBrush Fade(Color card, double solidAt, double clearAt, bool solidFirst)
    {
        var solid = Color.FromArgb(0xFF, card.R, card.G, card.B);
        var clear = Color.FromArgb(0x00, card.R, card.G, card.B);
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        brush.GradientStops.Add(new GradientStop(solidFirst ? solid : clear, solidFirst ? solidAt : clearAt));
        brush.GradientStops.Add(new GradientStop(solidFirst ? clear : solid, solidFirst ? clearAt : solidAt));
        brush.Freeze();
        return brush;
    }

    private static void ReplaceColors(ResourceDictionary resources, ResourceDictionary colors)
    {
        var merged = resources.MergedDictionaries;
        for (int i = 0; i < merged.Count; i++)
        {
            if (merged[i].Source?.OriginalString.EndsWith("Resources/Colors.xaml", StringComparison.OrdinalIgnoreCase) != true) continue;
            merged[i] = colors;
            return;
        }
        merged.Insert(0, colors);
    }

    /// <summary>Swaps in a copy of Shapes.xaml holding the theme's radii; the views follow because they name each radius with DynamicResource.</summary>
    private static void ReplaceShapes(ResourceDictionary resources, ResourceDictionary shapes)
    {
        var merged = resources.MergedDictionaries;
        for (int i = 0; i < merged.Count; i++)
        {
            if (merged[i].Source?.OriginalString.EndsWith("Resources/Shapes.xaml", StringComparison.OrdinalIgnoreCase) != true) continue;
            merged[i] = shapes;
            return;
        }
        merged.Add(shapes);
    }

    // ---- Auto ----------------------------------------------------------------------------------------------------------------

    /// <summary>What Auto picks now: High Contrast while Windows' contrast mode is on, else Light or Dark to match Windows.</summary>
    internal static string AutoThemeId() =>
        ThemeCatalog.WindowsInHighContrast ? ThemeCatalog.HighContrastId
        : WindowsPrefersLight() ? ThemeCatalog.LightId : ThemeCatalog.DarkId;

    /// <summary>True when Windows' "choose your default app mode" is Light.</summary>
    internal static bool WindowsPrefersLight()
    {
        return RegistryValues.Dword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", "Theme", "reading Windows' app mode") is int v && v != 0;
    }

    private void FollowWindows(bool follow)
    {
        if (follow == _followingWindows) return;
        _followingWindows = follow;
        if (follow) SystemEvents.UserPreferenceChanged += OnWindowsPreferenceChanged;
        else SystemEvents.UserPreferenceChanged -= OnWindowsPreferenceChanged;
    }

    private void OnWindowsPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (_selectedId != AutoId) return;
            var want = ThemeCatalog.BuiltIn(AutoThemeId())!;
            // High Contrast is read each time, so a change of contrast colors shows even though the name is the same.
            if (want.Id != _active.Id || want.Id == ThemeCatalog.HighContrastId) Show(want);
        });
    }

    // ---- Files -------------------------------------------------------------------------------------------------------------------

    private static bool CopyPicture(ResolvedBackground picture, string target)
    {
        try
        {
            // The source is opened first, so a picture that is not there leaves no empty file behind.
            // A picture of the theme's own is read only when it is a plain file name in the theme's folder, as the loader insists:
            // a path or network address in a theme file is not opened.
            string? source = picture.Embedded ? null : ThemeBackgroundLoader.BesideTheme(picture.Directory ?? string.Empty, picture.Image);
            if (!picture.Embedded && source == null) return false;
            using var input = picture.Embedded
                ? System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream($"ThemeImage/{picture.Image}")
                : File.OpenRead(source!);
            if (input == null) return false;
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            try
            {
                input.CopyTo(output);
            }
            catch
            {
                // Half a picture (a full disk) is not left in the Themes folder.
                output.Dispose();
                Undo(target);
                throw;
            }
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Theme", $"Could not copy the background picture for the new theme: {ex.Message}");
            return false;
        }
    }

    /// <summary>Takes away the files a failed <see cref="SaveTheme"/> made, so a retry does not leave a stray picture or theme behind.</summary>
    private static void Undo(params string?[] files)
    {
        foreach (string? file in files)
        {
            if (file == null) continue;
            try { File.Delete(file); }
            catch (Exception ex) { LoggingService.Swallowed("Theme", ex, "taking back a file a failed picture change made"); }
        }
    }

    private static System.Text.RegularExpressions.Regex NumberedPicture(string themeFile) =>
        new($@"^{System.Text.RegularExpressions.Regex.Escape(themeFile)}-(\d+)\.(jpg|jpeg|png|bmp)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>One more than the highest "Forest-N" picture in the folder, so the numbers say in which order pictures were added and a name is never reused.</summary>
    private static int NextPictureNumber(string directory, string themeFile)
    {
        var pattern = NumberedPicture(themeFile);
        int highest = 0;
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            var m = pattern.Match(Path.GetFileName(file));
            if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n > highest) highest = n;
        }
        return highest + 1;
    }

    // ---- Deleting a theme ------------------------------------------------------------------------------------------

    /// <summary>What <see cref="DeleteTheme"/> did: whether it worked, what to tell the user, and how many files went.</summary>
    internal sealed record DeleteOutcome(bool Ok, string Message, int Files);

    /// <summary>How a file leaves the Themes folder: the Recycle Bin, so a delete by mistake can be undone. The tests replace it.</summary>
    internal static Action<string> Discard { get; set; } = path =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

    /// <summary>Whether a file deleted at this path is kept in the Recycle Bin. The tests replace it.</summary>
    internal static Func<string, bool> RecycleBinIsOn { get; set; } = RecycleBinState.IsOnFor;

    /// <summary>The file of the user's theme with this id, even when it cannot be read as a theme (a typo in it); null when there is none.</summary>
    internal static string? UserThemeFile(string? id)
    {
        const string prefix = "user:";
        if (id == null || !id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string path = Path.Combine(ThemeCatalog.UserThemesDirectory, id[prefix.Length..] + ".theme.json");
        return File.Exists(path) ? path : null;
    }

    /// <summary>The theme file and the pictures that carry its name (Forest.jpg, Forest-1.png, Forest-background.jpg) that no other theme uses.</summary>
    internal static IReadOnlyList<string> FilesOfTheme(string themePath)
    {
        string directory = Path.GetDirectoryName(themePath)!;
        string baseName = ThemeFileReader.FileBaseName(themePath);
        var own = new System.Text.RegularExpressions.Regex(
            $@"^{System.Text.RegularExpressions.Regex.Escape(baseName)}(-\d+|-background(-\d+)?)?\.(jpg|jpeg|png|bmp)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var usedByOthers = ThemeCatalog.LoadUserThemes().Themes
            .Where(t => !string.Equals(t.Path, themePath, StringComparison.OrdinalIgnoreCase) && t.Background?.Image != null)
            .Select(t => t.Background!.Image!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // "Forest-1.jpg" is also the Forest theme's first picture: deleting a theme called Forest-1 must not take it.
        var numberedByOthers = Directory.EnumerateFiles(directory, "*.theme.json")
            .Where(p => !string.Equals(p, themePath, StringComparison.OrdinalIgnoreCase))
            .Select(p => NumberedPicture(ThemeFileReader.FileBaseName(p)))
            .ToList();

        var files = new List<string> { themePath };
        files.AddRange(Directory.EnumerateFiles(directory)
            .Where(f => own.IsMatch(Path.GetFileName(f)) && !usedByOthers.Contains(Path.GetFileName(f))
                        && !numberedByOthers.Any(other => other.IsMatch(Path.GetFileName(f)))));
        return files;
    }

    /// <summary>Sends one of the user's themes, and the pictures made for it, to the Recycle Bin (or deletes them, where the bin is off). A built-in theme cannot be deleted.</summary>
    internal DeleteOutcome DeleteTheme(string id)
    {
        string? themePath = UserThemeFile(id);
        if (themePath == null)
            return new DeleteOutcome(false, "Only a theme of your own can be deleted.", 0);

        string name = ThemeCatalog.Find(id)?.Name ?? ThemeFileReader.FileBaseName(themePath);
        int removed = 0;
        bool kept = RecycleBinIsOn(themePath);
        try
        {
            foreach (string file in FilesOfTheme(themePath))
            {
                Discard(file);
                removed++;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Theme", $"Deleting the theme {name} failed: {ex.Message}");
            return new DeleteOutcome(false, $"The theme could not be deleted: {ex.Message}", removed);
        }

        LoggingService.Info("Theme", $"Deleted the theme {name} ({removed} file(s), {(kept ? "to the Recycle Bin" : "for good: the Recycle Bin is off")}).");
        return new DeleteOutcome(true, kept ? $"{name} was moved to the Recycle Bin." : $"{name} was deleted.", removed);
    }

    /// <summary>"name", or "name 2", "name 3" ... when a theme file of that name is already there.</summary>
    internal static string FreeThemeName(string name)
    {
        string candidate = name;
        for (int i = 2; File.Exists(Path.Combine(ThemeCatalog.UserThemesDirectory, candidate + ".theme.json")); i++)
            candidate = $"{name} {i}";
        return candidate;
    }

    /// <summary>
    /// Watches the Themes folder, so a theme edited in a text editor shows as soon as it is saved and a file dropped
    /// in appears in the picker. Does nothing while the folder does not exist; whatever makes the folder calls this.
    /// </summary>
    public void WatchUserThemes()
    {
        try
        {
            lock (_watchLock)
            {
                WatchForTheFolder();

                string folder = ThemeCatalog.UserThemesDirectory;
                // A watcher left over from another folder (the Themes folder was moved) is not the one wanted.
                if (_watcher != null && !string.Equals(_watcher.Path, folder, StringComparison.OrdinalIgnoreCase)) DropWatcher();
                // Not created here: a PC that never makes a theme should not get an empty folder. Saving a theme makes it, then watches.
                if (_watcher != null || !Directory.Exists(folder)) return;
                _watcher = new FileSystemWatcher(folder)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };
                // A theme file or a picture beside it: either one changing means the theme on screen may have.
                static bool Relevant(string? file) =>
                    file != null && (file.EndsWith(".theme.json", StringComparison.OrdinalIgnoreCase) || ThemeBackgroundLoader.IsPictureFile(file));
                FileSystemEventHandler changed = (_, e) => { if (Relevant(e.Name)) ScheduleReload(); };
                _watcher.Changed += changed;
                _watcher.Created += changed;
                _watcher.Deleted += changed;
                _watcher.Renamed += (_, e) => { if (Relevant(e.Name) || Relevant(e.OldName)) ScheduleReload(); };
                // A watcher whose folder was deleted stops working for good; the one on the folder above makes a new one when it comes back.
                _watcher.Error += (_, _) => FolderCameOrWent();
            }
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("Theme", $"Not watching the Themes folder: {ex.Message}");
        }
    }

    private readonly object _watchLock = new();
    private FileSystemWatcher? _folderWatcher;

    /// <summary>Watches the folder that holds the Themes folder for it being deleted and made again (by hand, or by a restore), so the watching starts over.</summary>
    private void WatchForTheFolder()
    {
        string folder = ThemeCatalog.UserThemesDirectory;
        string? parent = Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar));
        if (parent == null) return;
        if (_folderWatcher != null && string.Equals(_folderWatcher.Path, parent, StringComparison.OrdinalIgnoreCase)) return;
        _folderWatcher?.Dispose();
        _folderWatcher = null;
        if (!Directory.Exists(parent)) return;

        string name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        _folderWatcher = new FileSystemWatcher(parent, name)
        {
            NotifyFilter = NotifyFilters.DirectoryName,
            EnableRaisingEvents = true
        };
        _folderWatcher.Created += (_, _) => FolderCameOrWent();
        _folderWatcher.Deleted += (_, _) => FolderCameOrWent();
        _folderWatcher.Renamed += (_, _) => FolderCameOrWent();
        _folderWatcher.Error += (_, _) => { lock (_watchLock) { _folderWatcher?.Dispose(); _folderWatcher = null; } };
    }

    private void DropWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    /// <summary>The Themes folder was deleted or made: start watching it afresh (when it is there), and look at what it holds.</summary>
    private void FolderCameOrWent()
    {
        lock (_watchLock) DropWatcher();
        WatchUserThemes();
        ScheduleReload();
    }

    private void ScheduleReload()
    {
        // An editor writes a file in several steps; wait for it to finish. One timer, restarted by each event:
        // events come in on pool threads, and making a new timer each time could leave one running unseen.
        lock (_debounceLock)
        {
            _debounce ??= new Timer(_ => Application.Current?.Dispatcher.InvokeAsync(Reload), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(400, Timeout.Infinite);
        }
    }

    private void Reload()
    {
        // A user theme that is selected is read again: an edit shows at once, and a file that stopped
        // being valid (or was deleted) falls back to Dark with the reason, then returns when it is fixed.
        if (_selectedId.StartsWith("user:", StringComparison.OrdinalIgnoreCase)) Apply(_selectedId);
        ThemesChanged?.Invoke(this, EventArgs.Empty);
    }
}
