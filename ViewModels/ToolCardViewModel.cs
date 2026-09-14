using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>One tool on the Tools page and in the tray's Tools submenu.</summary>
public sealed class ToolCardViewModel : ViewModelBase
{
    private BitmapImage? _iconImage;
    private bool _isMissing;

    public ToolCardViewModel(ToolEntry tool, ToolsViewModel owner)
    {
        Tool = tool;
        LaunchCommand = new RelayCommand(() => owner.Launch(this));
        ToggleFavoriteCommand = new RelayCommand(() => owner.ToggleFavorite(this));
        EditCommand = new RelayCommand(() => owner.Edit(this));
        RenameCommand = new RelayCommand(() => owner.Rename(this));
        ChangeCategoryCommand = new RelayCommand(() => owner.ChangeCategory(this));
        ChangeIconCommand = new RelayCommand(() => owner.ChangeIcon(this));
        OpenFolderCommand = new RelayCommand(() => owner.OpenFolder(this));
        RemoveCommand = new RelayCommand(() => owner.Remove(this));
        ToggleRunAsAdminCommand = new RelayCommand(() => owner.ToggleRunAsAdmin(this));
        Refresh();
    }

    public ToolEntry Tool { get; }

    public string Id => Tool.Id;
    public string Name => Tool.Name;
    public string Category => LibraryConstants.NormalizeCategory(Tool.Category);
    public string TargetPath => Tool.TargetPath;
    public bool IsFavorite => Tool.IsFavorite;
    public bool RunAsAdmin => Tool.RunAsAdmin;
    public string FavoriteMenuLabel => IsFavorite ? "Remove from Favorites" : "Add to Favorites";
    public string HotkeyDisplay => HotkeyManager.Normalize(Tool.Hotkey) ?? string.Empty;
    public bool HasHotkey => HotkeyDisplay.Length > 0;

    public BitmapImage? IconImage
    {
        get => _iconImage;
        private set => SetProperty(ref _iconImage, value);
    }

    public bool HasIcon => IconImage != null;

    public bool IsMissing
    {
        get => _isMissing;
        private set => SetProperty(ref _isMissing, value);
    }

    public ICommand LaunchCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand ChangeCategoryCommand { get; }
    public ICommand ChangeIconCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand ToggleRunAsAdminCommand { get; }

    /// <summary>Re-reads the icon and the program's presence, and refreshes every binding.</summary>
    public void Refresh()
    {
        IconImage = IconExtractorService.LoadBitmapSafely(Tool.IconPath, decodePixelWidth: 64);
        RefreshState();
    }

    /// <summary>Re-checks the program's presence and refreshes every binding, for a change that leaves the icon alone.</summary>
    public void RefreshState()
    {
        IsMissing = string.IsNullOrWhiteSpace(Tool.TargetPath) || !File.Exists(Tool.TargetPath);
        OnPropertyChanged(string.Empty);
    }
}
