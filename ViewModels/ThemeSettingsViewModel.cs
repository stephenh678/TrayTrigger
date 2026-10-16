using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Input;
using TrayTrigger.Services;
using TrayTrigger.Services.Theming;
using TrayTrigger.Views;

namespace TrayTrigger.ViewModels;

/// <summary>One entry of the theme picker.</summary>
/// <summary>One entry in the theme list; a separator is a line that cannot be chosen.</summary>
public sealed record ThemeChoice(string Id, string DisplayName, bool IsSeparator = false);

/// <summary>
/// Settings > General > Appearance: the theme picker, and the buttons for writing a theme of your own.
/// The choice is applied at once and saved with the settings. See docs/theming-plan.md.
/// </summary>
public sealed class ThemeSettingsViewModel : ViewModelBase
{
    private const string SeparatorId = "-separator-";

    private readonly Models.AppSettings _settings;
    private readonly Action _save;
    private readonly ThemeService _themes = ThemeService.Instance;

    public ThemeSettingsViewModel(Models.AppSettings settings, Action save)
    {
        _settings = settings;
        _save = save;
        EditCommand = new RelayCommand(Edit);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        ImportCommand = new RelayCommand(Import);
        DeleteCommand = new RelayCommand(Delete);
        _themes.ThemesChanged += OnThemesChanged;
        _themes.ThemeChanged += OnThemeChanged;
        // The picture is only read while the window is on screen, so a problem with it can turn up without a theme change.
        ThemeBackgroundState.Instance.PropertyChanged += (_, _) => RaiseStatus();
        Reload();
    }

    public ICommand EditCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand DeleteCommand { get; }

    /// <summary>True when the chosen theme is one of the user's: a built-in theme cannot be deleted.</summary>
    public bool CanDelete => _settings.ThemeId.StartsWith("user:", StringComparison.OrdinalIgnoreCase);

    /// <summary>The edit button's text: a built-in theme opens as a copy, so it says it makes a new theme.</summary>
    public string EditLabel => CanDelete ? "Edit theme..." : "New theme from this...";

    public IReadOnlyList<ThemeChoice> Themes { get; private set; } = Array.Empty<ThemeChoice>();

    /// <summary>The id of the chosen theme. Setting it puts that theme on screen and saves the choice.</summary>
    public string SelectedThemeId
    {
        get => _settings.ThemeId;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _settings.ThemeId) return;
            _settings.ThemeId = value;
            _themes.Apply(value);
            _save();
            PictureMessage = string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanDelete));
            OnPropertyChanged(nameof(EditLabel));
            RaiseStatus();
        }
    }

    /// <summary>Whether the theme's background picture is shown. One setting for every theme.</summary>
    public bool ShowBackground
    {
        get => _settings.ShowThemeBackground;
        set
        {
            if (value == _settings.ShowThemeBackground) return;
            _settings.ShowThemeBackground = value;
            _themes.ShowBackground = value;
            _save();
            OnPropertyChanged();
            RaiseStatus();
        }
    }

    public int MinTextSize => Services.AppTypography.MinScalePercent;
    public int MaxTextSize => Services.AppTypography.MaxScalePercent;

    /// <summary>
    /// How large the app's text is, in percent: 100 is as designed. It is the size for every theme that does not set its own;
    /// setting it puts it on screen at once and saves it.
    /// </summary>
    public int TextSize
    {
        get => Math.Clamp(_settings.TextSizePercent, MinTextSize, MaxTextSize);
        set
        {
            int percent = Math.Clamp(value, MinTextSize, MaxTextSize);
            if (percent != TextSize)
            {
                _settings.TextSizePercent = percent;
                Services.AppTypography.SystemPercent = percent;
                Services.AppTypography.ApplyForTheme(_themes.Active.TextSize);
                _save();
            }
            // Raised even when nothing changed: a number typed past the range comes back as the one it was brought to.
            OnPropertyChanged();
            OnPropertyChanged(nameof(TextSizeIsLarge));
            OnPropertyChanged(nameof(CanResetTextSize));
        }
    }

    /// <summary>True past the size every screen is checked at: the window says some screens may clip text.</summary>
    public bool TextSizeIsLarge => TextSize > Services.AppTypography.RecommendedMaxScalePercent;

    public string TextSizeWarning => $"Past {Services.AppTypography.RecommendedMaxScalePercent}% some screens may clip text. Lower the size to fix it.";

    /// <summary>The fonts that can be chosen (any installed font but the symbol fonts).</summary>
    public IReadOnlyList<string> Fonts => Services.AppFonts.Installed;

    /// <summary>The chosen font. Setting it puts it on screen at once and saves it; the default is saved as nothing.</summary>
    public string FontName
    {
        get => Services.AppFonts.Find(_settings.FontName) ?? Services.AppFonts.DefaultName;
        set
        {
            string chosen = Services.AppFonts.Find(value) ?? Services.AppFonts.DefaultName;
            string stored = chosen == Services.AppFonts.DefaultName ? string.Empty : chosen;
            if (stored != _settings.FontName)
            {
                _settings.FontName = stored;
                Services.AppFonts.SystemName = stored;
                Services.AppFonts.ApplyForTheme(_themes.Active.Font);
                _save();
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanResetFont));
        }
    }

    /// <summary>Says so when the theme on screen sets its own font: this one is not used while that theme is on, so it is greyed out.</summary>
    public string FontNote => Services.AppFonts.Find(_themes.Active.Font) is { } own
        ? $"{_themes.Active.Name} sets its own font ({own}), so this setting is not used while it is on. Choose another theme to change it."
        : string.Empty;

    public bool HasFontNote => FontNote.Length > 0;

    /// <summary>False while the theme on screen has a font of its own: a change here would show nothing, so the list is greyed out.</summary>
    public bool FontCanBeSet => !HasFontNote;

    public bool CanResetFont => FontName != Services.AppFonts.DefaultName;

    public ICommand ResetFontCommand => _resetFont ??= new RelayCommand(() => FontName = Services.AppFonts.DefaultName);
    private ICommand? _resetFont;

    public bool CanResetTextSize => TextSize != 100;

    public ICommand ResetTextSizeCommand => _resetTextSize ??= new RelayCommand(() => TextSize = 100);
    private ICommand? _resetTextSize;

    /// <summary>Says so when the theme on screen sets its own text size: this one is not used while that theme is on, so it is greyed out.</summary>
    public string TextSizeNote => _themes.Active.TextSize is { } own
        ? $"{_themes.Active.Name} sets its own text size ({own}%), so this setting is not used while it is on. Choose another theme to change it."
        : string.Empty;

    public bool HasTextSizeNote => TextSizeNote.Length > 0;

    /// <summary>False while the theme on screen has a text size of its own: a change here would show nothing, so the slider is greyed out.</summary>
    public bool TextSizeCanBeSet => !HasTextSizeNote;

    /// <summary>What to tell the user about the theme: why it is not the one chosen, what is hard to read in it, which files were skipped.</summary>
    public string Status
    {
        get
        {
            var lines = new List<string>();
            if (_themes.FallbackReason is { } reason) lines.Add(reason);
            if (_themes.BackgroundProblem is { } picture) lines.Add("Background picture: " + picture);
            lines.AddRange(_themes.ThemeWarnings.Select(w => "In the theme file: " + w));
            lines.AddRange(_themes.ContrastWarnings.Select(w => "Hard to read: " + w));
            lines.AddRange(_themes.Problems.Select(p => "Not loaded: " + p));
            return string.Join('\n', lines);
        }
    }

    public bool HasStatus => Status.Length > 0;

    private string _pictureMessage = string.Empty;

    /// <summary>What happened to the picture just chosen: its size and anything worth knowing about it.</summary>
    public string PictureMessage
    {
        get => _pictureMessage;
        private set { _pictureMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasPictureMessage)); }
    }

    public bool HasPictureMessage => _pictureMessage.Length > 0;

    /// <summary>What the dropdown's selection says when it is not a plain name: Auto shows the theme it picked.</summary>
    public string Summary => _themes.SelectedId == ThemeService.AutoId
        ? $"Following Windows: {_themes.ActiveName} right now."
        : _themes.IsDark ? "A dark theme." : "A light theme.";

    private void OnThemesChanged(object? sender, EventArgs e)
    {
        Reload();
        RaiseStatus();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => RaiseStatus();

    private void Reload()
    {
        var choices = _themes.Available()
            .Select(t => new ThemeChoice(t.Id, t.Id == ThemeCatalog.DarkId ? t.Name + " (Default)" : t.BuiltIn ? t.Name : t.Name + " (yours)"))
            .ToList();
        // A line between the built-in themes and the player's own, when there are any.
        int firstOwn = _themes.Available().ToList().FindIndex(t => !t.BuiltIn);
        if (firstOwn > 0) choices.Insert(firstOwn, new ThemeChoice(SeparatorId, string.Empty, IsSeparator: true));
        Themes = choices;
        OnPropertyChanged(nameof(Themes));
        // The list was rebuilt, so tell the combo box which entry is selected again.
        OnPropertyChanged(nameof(SelectedThemeId));
    }

    private void RaiseStatus()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(TextSizeNote));
        OnPropertyChanged(nameof(HasTextSizeNote));
        OnPropertyChanged(nameof(TextSizeCanBeSet));
        OnPropertyChanged(nameof(FontNote));
        OnPropertyChanged(nameof(HasFontNote));
        OnPropertyChanged(nameof(FontCanBeSet));
    }

    // ---- Your own theme ---------------------------------------------------------------------------------

    private void Edit()
    {
        // The chosen theme; for Auto, the one Auto picked. A built-in theme opens as a copy that is saved as a theme of the user's.
        var source = _themes.SelectedId == ThemeService.AutoId ? _themes.Active : ThemeCatalog.Find(_themes.SelectedId);
        if (source == null)
        {
            ModernDialog.ShowWarning(WindowHelper.ActiveOwner(), "This theme cannot be opened",
                _themes.FallbackReason ?? "Its theme file could not be read. Open the themes folder to fix it or remove it.");
            return;
        }

        var dialog = new ThemeEditDialog(new ThemeEditorViewModel(source, _themes));
        if (dialog.ShowDialog() != true || dialog.SavedPath == null) return;

        string id = ThemeCatalog.UserThemeId(dialog.SavedPath);
        Reload();
        // A new theme becomes the chosen one; one that was edited is put on screen again, with its changes.
        if (id != _settings.ThemeId) SelectedThemeId = id;
        else _themes.Apply(id);
        RaiseStatus();
    }

    private void Import()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import a theme",
            Filter = "Theme and picture files (*.json;*.jpg;*.jpeg;*.png)|*.json;*.jpg;*.jpeg;*.png",
            CheckFileExists = true,
            Multiselect = true,
        };
        if (FileDialogCloak.Show(dialog) != true) return;

        var outcome = _themes.ImportThemeFiles(dialog.FileNames);
        if (!outcome.Ok)
        {
            ModernDialog.ShowWarning(WindowHelper.ActiveOwner(), "The theme was not imported", outcome.Message);
            return;
        }

        Reload();
        SelectedThemeId = ThemeCatalog.UserThemeId(outcome.ThemePath!);
        PictureMessage = outcome.Message;
    }

    private void Delete()
    {
        if (!CanDelete) return;
        string id = _settings.ThemeId;

        string? file = ThemeService.UserThemeFile(id);
        if (file == null)
        {
            // The chosen theme's file is gone (removed in Explorer): nothing to delete, so leave it and go back to Dark.
            Reload();
            SelectedThemeId = ThemeCatalog.DarkId;
            PictureMessage = "That theme's file was already gone.";
            return;
        }

        // A file with a typo in it is not a theme to the app, but it can still be deleted by its name.
        string name = ThemeCatalog.Find(id)?.Name ?? ThemeFileReader.FileBaseName(file);
        int files = ThemeService.FilesOfTheme(file).Count;
        string detail = DeleteDetail(files, ThemeService.RecycleBinIsOn(file));
        if (!ModernDialog.ConfirmDelete(WindowHelper.ActiveOwner(), "Delete theme", $"Delete the theme \"{name}\"?", detail, "Delete")) return;

        var outcome = _themes.DeleteTheme(id);
        if (!outcome.Ok)
        {
            ModernDialog.ShowWarning(WindowHelper.ActiveOwner(), "Could not delete the theme", outcome.Message);
            return;
        }

        Reload();
        SelectedThemeId = ThemeCatalog.DarkId;
        PictureMessage = outcome.Message;
    }

    /// <summary>What the delete prompt says happens to the files: they go to the Recycle Bin, or are gone for good where the bin is off.</summary>
    internal static string DeleteDetail(int files, bool recycleBinOn)
    {
        string what = files > 1 ? $"Its theme file and {files - 1} picture{(files == 2 ? "" : "s")}" : "Its theme file";
        string verb = files > 1 ? "go" : "goes";
        string areIs = files > 1 ? "are" : "is";
        return recycleBinOn
            ? $"{what} {verb} to the Recycle Bin."
            : $"{what} {areIs} deleted for good, because the Recycle Bin is turned off for this drive.";
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(ThemeCatalog.UserThemesDirectory);
            // The folder may be new: from here on, a file dropped into it shows up in the picker.
            _themes.WatchUserThemes();
            Process.Start(new ProcessStartInfo(ThemeCatalog.UserThemesDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) { LoggingService.Warn("Theme", $"Could not open the Themes folder: {ex.Message}"); }
    }
}
