using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>The Edit Tool dialog: name, category, favorite, icon, program, folder, arguments, admin, hotkey, and whether it starts with games or closes for them.</summary>
public sealed class ToolEditViewModel : ViewModelBase
{
    private readonly ToolEntry _tool;
    private readonly IconExtractorService _icons;

    private string _name;
    private string _category;
    private string _targetPath;
    private string _arguments;
    private string _workingDirectory;
    private string _hotkey;
    private bool _runAsAdmin;
    private bool _hideWindow;
    private bool _isFavorite;
    private bool _startWithGames;
    private bool _waitBeforeGame;
    private string _waitBeforeGameSeconds;
    private bool _closeAfterGames;
    private bool _closeForGames;
    private bool _reopenAfterGames;
    private string _validationMessage = string.Empty;
    private string? _pendingIconSource;
    private BitmapImage? _iconPreview;
    private string _iconNote = string.Empty;

    public ToolEditViewModel(ToolEntry tool, IEnumerable<string> categories, IconExtractorService icons)
    {
        _tool = tool;
        _icons = icons;
        _name = tool.Name;
        _category = ToolCatalog.NormalizeCategory(tool.Category);
        _targetPath = tool.TargetPath;
        _arguments = tool.Arguments;
        _workingDirectory = tool.WorkingDirectory;
        _hotkey = tool.Hotkey;
        _runAsAdmin = tool.RunAsAdmin;
        _hideWindow = tool.HideWindow;
        _isFavorite = tool.IsFavorite;
        _startWithGames = tool.StartWithGames;
        _waitBeforeGame = tool.WaitBeforeGame;
        _waitBeforeGameSeconds = tool.WaitBeforeGameSeconds.ToString();
        _closeAfterGames = tool.CloseAfterGames;
        _closeForGames = tool.CloseForGames;
        _reopenAfterGames = tool.ReopenAfterGames;
        ExistingCategories = categories.ToList();
        _iconPreview = IconExtractorService.LoadBitmapSafely(tool.IconPath, decodePixelWidth: 64);

        BrowseTargetCommand = new RelayCommand(BrowseTarget);
        BrowseWorkDirCommand = new RelayCommand(BrowseWorkDir);
        ChangeIconCommand = new RelayCommand(PickIcon);
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
        _initialEditState = EditState();
    }

    private readonly string _initialEditState;

    /// <summary>
    /// True once any field Save writes back differs from what the dialog opened with. Esc asks
    /// before throwing such edits away; with nothing changed it closes at once.
    /// </summary>
    public bool HasUnsavedChanges => !string.Equals(EditState(), _initialEditState, StringComparison.Ordinal);

    /// <summary>Every value Save copies onto the tool, joined into one comparable string.</summary>
    private string EditState() => string.Join("", new object?[]
    {
        Name, Category, TargetPath, Arguments, WorkingDirectory, Hotkey, RunAsAdmin, HideWindow, IsFavorite,
        StartWithGames, WaitBeforeGame, WaitBeforeGameSeconds, CloseAfterGames, CloseForGames, ReopenAfterGames, _pendingIconSource,
    });

    public event Action<bool>? RequestClose;

    /// <summary>The recorder's owner id, so re-recording this tool's own hotkey isn't a conflict.</summary>
    public string OwnerId => _tool.Id;
    public IReadOnlyList<string> ExistingCategories { get; }

    /// <summary>A Store app: shown by its app ID, with no program, working folder or Run as Administrator to edit.</summary>
    public bool IsStoreApp => ToolCatalog.IsStoreApp(_tool);
    public bool IsProgram => !IsStoreApp;
    public string AppId => _tool.AppId;

    public string Name
    {
        get => _name;
        set
        {
            SetProperty(ref _name, value ?? string.Empty);
            OnPropertyChanged(nameof(HeadingText));
        }
    }

    /// <summary>The dialog's heading: the tool being edited, or "New Tool" until it has a name.</summary>
    public string HeadingText => string.IsNullOrWhiteSpace(Name) ? "New Tool" : Name.Trim();
    public string Category { get => _category; set => SetProperty(ref _category, value ?? string.Empty); }
    public string TargetPath
    {
        get => _targetPath;
        set
        {
            if (SetProperty(ref _targetPath, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsScript));
                OnPropertyChanged(nameof(CanStartWithGames));
            }
        }
    }

    /// <summary>The target is a script, so Hide Window applies. Follows the path as it's edited.</summary>
    public bool IsScript => IsProgram && ToolCatalog.IsScriptPath(TargetPath);
    /// <summary>A program, not a script or Store app, so it can start with games (<see cref="ToolCatalog.CanStartWithGames"/>). Follows the path as it's edited.</summary>
    public bool CanStartWithGames => IsProgram && !IsScript;
    /// <summary>
    /// "When I launch a game": do nothing (the default), start the tool, or close it. One of three, as
    /// checkboxes: ticking one clears the others, and clearing an action falls back to Do nothing, which
    /// can't itself be un-ticked. Each action has its own options beneath it.
    /// </summary>
    public bool LeaveAlone
    {
        get => !_startWithGames && !_closeForGames;
        set
        {
            if (value)
            {
                StartWithGames = false;
                CloseForGames = false;
            }
            // Un-ticking the default, or ticking it when it already is: the box shows it ticked still.
            OnPropertyChanged(nameof(LeaveAlone));
        }
    }

    public bool StartWithGames
    {
        get => _startWithGames;
        set
        {
            if (!SetProperty(ref _startWithGames, value)) return;
            if (value) CloseForGames = false;
            OnPropertyChanged(nameof(LeaveAlone));
        }
    }

    public bool CloseForGames
    {
        get => _closeForGames;
        set
        {
            if (!SetProperty(ref _closeForGames, value)) return;
            if (value) StartWithGames = false;
            OnPropertyChanged(nameof(LeaveAlone));
        }
    }

    /// <summary>Only saved with <see cref="CloseForGames"/>; the dialog disables it otherwise.</summary>
    public bool ReopenAfterGames { get => _reopenAfterGames; set => SetProperty(ref _reopenAfterGames, value); }
    /// <summary>Only saved with <see cref="StartWithGames"/>, like <see cref="CloseAfterGames"/>; the dialog disables both otherwise.</summary>
    public bool WaitBeforeGame { get => _waitBeforeGame; set => SetProperty(ref _waitBeforeGame, value); }
    /// <summary>Text, as typed: Save checks it only while the wait is on, and blank means the default.</summary>
    public string WaitBeforeGameSeconds { get => _waitBeforeGameSeconds; set => SetProperty(ref _waitBeforeGameSeconds, value ?? string.Empty); }
    public bool CloseAfterGames { get => _closeAfterGames; set => SetProperty(ref _closeAfterGames, value); }
    public bool HideWindow { get => _hideWindow; set => SetProperty(ref _hideWindow, value); }
    public string Arguments { get => _arguments; set => SetProperty(ref _arguments, value ?? string.Empty); }
    public string WorkingDirectory { get => _workingDirectory; set => SetProperty(ref _workingDirectory, value ?? string.Empty); }
    public string Hotkey { get => _hotkey; set => SetProperty(ref _hotkey, value ?? string.Empty); }
    public bool RunAsAdmin { get => _runAsAdmin; set => SetProperty(ref _runAsAdmin, value); }
    public bool IsFavorite { get => _isFavorite; set => SetProperty(ref _isFavorite, value); }

    public BitmapImage? IconPreview
    {
        get => _iconPreview;
        private set
        {
            if (SetProperty(ref _iconPreview, value)) OnPropertyChanged(nameof(HasIconPreview));
        }
    }

    public bool HasIconPreview => IconPreview != null;

    public string IconNote
    {
        get => _iconNote;
        private set => SetProperty(ref _iconNote, value);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value)) OnPropertyChanged(nameof(HasValidationMessage));
        }
    }

    public bool HasValidationMessage => ValidationMessage.Length > 0;

    public ICommand BrowseTargetCommand { get; }
    public ICommand BrowseWorkDirCommand { get; }
    public ICommand ChangeIconCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    private void BrowseTarget()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Program or Script",
            Filter = "Programs and scripts (*.exe;*.bat;*.cmd;*.ps1)|*.exe;*.bat;*.cmd;*.ps1",
            CheckFileExists = true
        };
        if (FileDialogCloak.Show(dialog) != true) return;
        WorkingDirectory = ToolCatalog.WorkingDirectoryAfterRetarget(WorkingDirectory, TargetPath, dialog.FileName);
        TargetPath = dialog.FileName;
    }

    private void BrowseWorkDir()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Working Folder",
            Multiselect = false
        };
        if (FileDialogCloak.Show(dialog) == true)
        {
            WorkingDirectory = dialog.FolderName;
        }
    }

    private void PickIcon()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Icon",
            Filter = "Image & Icon Files (*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.exe)|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.exe|All Files (*.*)|*.*",
            CheckFileExists = true
        };
        if (FileDialogCloak.Show(dialog) != true) return;

        _pendingIconSource = dialog.FileName;
        string ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".ico")
        {
            IconPreview = IconExtractorService.LoadBitmapSafely(dialog.FileName, decodePixelWidth: 64);
            IconNote = string.Empty;
        }
        else
        {
            IconNote = "The icon updates when you save.";
        }
    }

    private void Save()
    {
        string name = Name.Trim();
        if (name.Length == 0)
        {
            ValidationMessage = "Give the tool a name.";
            return;
        }

        string target = TargetPath.Trim();
        bool targetChanged = false;
        if (IsProgram)
        {
            string? problem = ToolCatalog.ValidateTarget(target);
            if (problem != null)
            {
                ValidationMessage = $"This file can't be used because {problem}.";
                return;
            }
            bool startWithGames = !ToolCatalog.IsScriptPath(target) && StartWithGames;
            bool closeForGames = !ToolCatalog.IsScriptPath(target) && !startWithGames && CloseForGames;
            if (!TryReadWaitSeconds(startWithGames && WaitBeforeGame, out int waitSeconds)) return;
            targetChanged = !string.Equals(target, _tool.TargetPath, StringComparison.OrdinalIgnoreCase);

            _tool.TargetPath = target;
            _tool.WorkingDirectory = WorkingDirectory.Trim();
            _tool.RunAsAdmin = RunAsAdmin;
            _tool.HideWindow = ToolCatalog.IsScriptPath(target) && HideWindow;
            _tool.StartWithGames = startWithGames;
            _tool.WaitBeforeGame = startWithGames && WaitBeforeGame;
            _tool.WaitBeforeGameSeconds = waitSeconds;
            _tool.CloseAfterGames = startWithGames && CloseAfterGames;
            _tool.CloseForGames = closeForGames;
            _tool.ReopenAfterGames = closeForGames && ReopenAfterGames;
        }

        _tool.Name = name;
        _tool.Category = ToolCatalog.NormalizeCategory(Category);
        _tool.Arguments = Arguments.Trim();
        _tool.IsFavorite = IsFavorite;
        _tool.Hotkey = HotkeyManager.Normalize(Hotkey) ?? string.Empty;

        // A chosen icon wins; otherwise a new program brings its own icon. A failed extraction keeps the old one.
        string? iconSource = _pendingIconSource ?? (targetChanged ? target : null);
        if (iconSource != null)
        {
            string cached = _icons.ExtractAndCacheIcon(_tool.Id, iconSource, name);
            if (!string.IsNullOrEmpty(cached)) _tool.IconPath = cached;
        }

        LoggingService.Info("Tools", $"Saved tool '{name}' ('{ToolCatalog.LaunchDisplay(_tool)}', category '{_tool.Category}', run as admin {RunAsAdmin}, hotkey '{_tool.Hotkey}', start with games {_tool.StartWithGames}, wait {(_tool.WaitBeforeGame ? $"{_tool.WaitBeforeGameSeconds}s" : "off")}, close after games {_tool.CloseAfterGames}, close for games {_tool.CloseForGames}{(_tool.ReopenAfterGames ? " and open again" : string.Empty)}).");
        RequestClose?.Invoke(true);
    }

    /// <summary>
    /// The seconds to save: what was typed, blank meaning the default. Checked only while the wait is
    /// on: with it off, text that isn't a valid number keeps the value saved before. False, with the
    /// reason shown, when the wait is on and the number is outside 1-60.
    /// </summary>
    private bool TryReadWaitSeconds(bool waiting, out int seconds)
    {
        string text = WaitBeforeGameSeconds.Trim();
        if (text.Length == 0)
        {
            seconds = ToolCatalog.DefaultWaitSeconds;
            return true;
        }
        if (int.TryParse(text, out seconds) && seconds >= ToolCatalog.MinWaitSeconds && seconds <= ToolCatalog.MaxWaitSeconds)
        {
            return true;
        }
        if (!waiting)
        {
            seconds = _tool.WaitBeforeGameSeconds;
            return true;
        }
        ValidationMessage = $"The wait before starting the game must be a whole number of seconds between {ToolCatalog.MinWaitSeconds} and {ToolCatalog.MaxWaitSeconds}.";
        return false;
    }
}
