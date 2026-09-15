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

/// <summary>The Edit Tool dialog: name, category, favorite, icon, program, folder, arguments, admin and hotkey.</summary>
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
    private bool _isFavorite;
    private string _validationMessage = string.Empty;
    private string? _pendingIconSource;
    private BitmapImage? _iconPreview;
    private string _iconNote = string.Empty;

    public ToolEditViewModel(ToolEntry tool, IEnumerable<string> categories, IconExtractorService icons)
    {
        _tool = tool;
        _icons = icons;
        _name = tool.Name;
        _category = LibraryConstants.NormalizeCategory(tool.Category);
        _targetPath = tool.TargetPath;
        _arguments = tool.Arguments;
        _workingDirectory = tool.WorkingDirectory;
        _hotkey = tool.Hotkey;
        _runAsAdmin = tool.RunAsAdmin;
        _isFavorite = tool.IsFavorite;
        ExistingCategories = categories.ToList();
        _iconPreview = IconExtractorService.LoadBitmapSafely(tool.IconPath, decodePixelWidth: 64);

        BrowseTargetCommand = new RelayCommand(BrowseTarget);
        BrowseWorkDirCommand = new RelayCommand(BrowseWorkDir);
        ChangeIconCommand = new RelayCommand(PickIcon);
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
    }

    public event Action<bool>? RequestClose;

    /// <summary>The recorder's owner id, so re-recording this tool's own hotkey isn't a conflict.</summary>
    public string OwnerId => _tool.Id;
    public IReadOnlyList<string> ExistingCategories { get; }

    /// <summary>A Store app: shown by its app ID, with no program, working folder or Run as Administrator to edit.</summary>
    public bool IsStoreApp => ToolCatalog.IsStoreApp(_tool);
    public bool IsProgram => !IsStoreApp;
    public string AppId => _tool.AppId;

    public string Name { get => _name; set => SetProperty(ref _name, value ?? string.Empty); }
    public string Category { get => _category; set => SetProperty(ref _category, value ?? string.Empty); }
    public string TargetPath { get => _targetPath; set => SetProperty(ref _targetPath, value ?? string.Empty); }
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
            Title = "Select Program",
            Filter = "Programs (*.exe)|*.exe",
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
                ValidationMessage = $"This program can't be used because {problem}.";
                return;
            }
            targetChanged = !string.Equals(target, _tool.TargetPath, StringComparison.OrdinalIgnoreCase);

            _tool.TargetPath = target;
            _tool.WorkingDirectory = WorkingDirectory.Trim();
            _tool.RunAsAdmin = RunAsAdmin;
        }

        _tool.Name = name;
        _tool.Category = LibraryConstants.NormalizeCategory(Category);
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

        LoggingService.Info("Tools", $"Saved tool '{name}' ('{ToolCatalog.LaunchDisplay(_tool)}', category '{_tool.Category}', run as admin {RunAsAdmin}, hotkey '{_tool.Hotkey}').");
        RequestClose?.Invoke(true);
    }
}
