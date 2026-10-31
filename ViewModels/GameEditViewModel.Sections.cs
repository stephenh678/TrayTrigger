using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>Edit Game: the tabs, and the per-field error messages.</summary>
public partial class GameEditViewModel
{
    // --- Tabs (All + one per card, the same pattern as Settings and System) ---

    private GameEditSection _selectedSection = GameEditSection.All;

    /// <summary>
    /// The tab on show. Every card stays in the visual tree and only its visibility follows this,
    /// so bindings, validation state and unsaved edits live on across tab switches.
    /// </summary>
    public GameEditSection SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (SetProperty(ref _selectedSection, value))
            {
                OnPropertyChanged(nameof(IsAllSection));
                OnPropertyChanged(nameof(IsIdentitySection));
                OnPropertyChanged(nameof(IsArtworkSection));
                OnPropertyChanged(nameof(IsLaunchSection));
                OnPropertyChanged(nameof(IsPerformanceSection));
                OnPropertyChanged(nameof(IsDisplaySection));
                OnPropertyChanged(nameof(IsScriptsSection));
                OnPropertyChanged(nameof(ShowIdentityCard));
                OnPropertyChanged(nameof(ShowArtworkCard));
                OnPropertyChanged(nameof(ShowLaunchCard));
                OnPropertyChanged(nameof(ShowPerformanceCard));
                OnPropertyChanged(nameof(ShowDisplayCard));
                OnPropertyChanged(nameof(ShowScreensCard));
                OnPropertyChanged(nameof(ShowCoresAndHdrCard));
                // The DLSS card shares the Performance tab; it owns its own visibility so the
                // XAML needs one binding rather than a multi-binding across two view models.
                Dlss.IsOnTab = IsAllSection || IsPerformanceSection;
                OnPropertyChanged(nameof(ShowScriptsCardOnTab));
                OnPropertyChanged(nameof(ShowScriptsStubOnTab));
            }
        }
    }

    public bool IsAllSection => SelectedSection == GameEditSection.All;
    public bool IsIdentitySection => SelectedSection == GameEditSection.Identity;
    public bool IsArtworkSection => SelectedSection == GameEditSection.Artwork;
    public bool IsLaunchSection => SelectedSection == GameEditSection.Launch;
    public bool IsPerformanceSection => SelectedSection == GameEditSection.Performance;
    public bool IsDisplaySection => SelectedSection == GameEditSection.Display;
    public bool IsScriptsSection => SelectedSection == GameEditSection.Scripts;

    public bool ShowIdentityCard => IsAllSection || IsIdentitySection;
    public bool ShowArtworkCard => IsAllSection || IsArtworkSection;
    public bool ShowLaunchCard => IsAllSection || IsLaunchSection;
    public bool ShowPerformanceCard => IsAllSection || IsPerformanceSection;

    /// <summary>CPU cores, HDR and Auto HDR each come and go with this PC; with none of them, the card is left out.</summary>
    public bool ShowCoresAndHdrCard => ShowPerformanceCard && (ShowCpuCores || ShowHdr || ShowAutoHdr);

    /// <summary>The mouse and dimming options only exist with more than one monitor, so the Screens card does too.</summary>
    public bool ShowScreensCard => ShowDisplayCard && (ShowMouseLock || ShowDim);

    private bool _showScriptRunOptions;

    /// <summary>The "How the scripts run" section: shut for a game with no script, open once it has one or a value in it is refused.</summary>
    public bool ShowScriptRunOptions
    {
        get => _showScriptRunOptions;
        set => SetProperty(ref _showScriptRunOptions, value);
    }
    public bool ShowScriptsCardOnTab => ShowScriptsCard && (IsAllSection || IsScriptsSection);
    /// <summary>The "scripts are turned off" note shows on the Scripts tab, and last on All.</summary>
    public bool ShowScriptsStubOnTab => ShowScriptsStub && (IsAllSection || IsScriptsSection);

    public ICommand SelectSectionCommand { get; }

    /// <summary>The tabs in strip order. Scripts is always there: while scripts are off it holds the note about turning them on.</summary>
    public IReadOnlyList<GameEditSection> AvailableSections { get; } =
        [GameEditSection.All, GameEditSection.Identity, GameEditSection.Artwork, GameEditSection.Launch, GameEditSection.Performance, GameEditSection.Display, GameEditSection.Scripts];

    /// <summary>Ctrl+Tab and Ctrl+Shift+Tab: the next or previous tab, wrapping at the ends.</summary>
    public void MoveSection(int step)
    {
        var sections = AvailableSections;
        int index = Math.Max(0, sections.ToList().IndexOf(SelectedSection));
        SelectedSection = sections[((index + step) % sections.Count + sections.Count) % sections.Count];
    }

    /// <summary>The fields Save can reject, so the dialog can bring the right one into view.</summary>
    public enum EditField { SteamAppId, PreLaunchScript, PostExitScript, PreLaunchTimeout, CpuCoresDelay }

    /// <summary>Raised when Save refuses a value. The tab has already been switched to show it.</summary>
    public event Action<EditField>? ValidationFailed;

    internal static GameEditSection SectionOf(EditField field) => field switch
    {
        EditField.SteamAppId => GameEditSection.Identity,
        EditField.CpuCoresDelay => GameEditSection.Performance,
        _ => GameEditSection.Scripts
    };

    /// <summary>
    /// Puts the refused field on screen: the tab you are on if it already shows it (All shows
    /// everything), otherwise the tab that holds it. Then tells the dialog which field it was.
    /// </summary>
    private void RejectField(EditField field, string message)
    {
        // The reason goes under the field itself, where the eye already is once focus moves
        // there; the footer only says the save didn't happen.
        SetFieldError(field, message);
        // The App ID box sits behind "Enter an App ID": a refused ID opens it.
        if (field == EditField.SteamAppId) ShowAppIdEntry = true;
        if (field == EditField.PreLaunchTimeout) ShowScriptRunOptions = true;
        ShowProblem("Not saved: one field needs fixing. The reason is under it.");
        var section = SectionOf(field);
        if (!IsAllSection && SelectedSection != section) SelectedSection = section;
        ValidationFailed?.Invoke(field);
    }

    private (EditField Field, string Message)? _fieldError;

    /// <summary>Why Save refused this field, shown under it until the field is edited; else null.</summary>
    public string? SteamAppIdError => ErrorFor(EditField.SteamAppId);
    public string? PreLaunchScriptError => ErrorFor(EditField.PreLaunchScript);
    public string? PostExitScriptError => ErrorFor(EditField.PostExitScript);
    public string? PreLaunchTimeoutError => ErrorFor(EditField.PreLaunchTimeout);
    public string? CpuCoresDelayError => ErrorFor(EditField.CpuCoresDelay);

    private string? ErrorFor(EditField field) => _fieldError is { } e && e.Field == field ? e.Message : null;

    private void SetFieldError(EditField? field, string? message = null)
    {
        EditField? previous = _fieldError?.Field;
        _fieldError = field is { } f ? (f, message ?? string.Empty) : null;
        if (previous is { } p) OnPropertyChanged(ErrorPropertyOf(p));
        if (field is { } now && now != previous) OnPropertyChanged(ErrorPropertyOf(now));
    }

    /// <summary>Editing the refused field takes its message away.</summary>
    private void ClearFieldError(EditField field)
    {
        if (_fieldError?.Field == field) SetFieldError(null);
    }

    private static string ErrorPropertyOf(EditField field) => field switch
    {
        EditField.SteamAppId => nameof(SteamAppIdError),
        EditField.PreLaunchScript => nameof(PreLaunchScriptError),
        EditField.PostExitScript => nameof(PostExitScriptError),
        EditField.PreLaunchTimeout => nameof(PreLaunchTimeoutError),
        _ => nameof(CpuCoresDelayError),
    };

    public string PreLaunchScriptPath
    {
        get => _preLaunchScriptPath;
        set
        {
            _preLaunchScriptPath = value;
            OnPropertyChanged();
            ClearFieldError(EditField.PreLaunchScript);
            OnPropertyChanged(nameof(HasPreLaunchScript));
            OnPropertyChanged(nameof(HasAnyScript));
            OnPropertyChanged(nameof(CanEditScriptArguments));
            OnPropertyChanged(nameof(DefaultScriptsSummary));
            // Typing, Browse and "New script..." all land here, so the post-exit box follows along.
            if (_useSameScriptForBoth) PostExitScriptPath = value;
        }
    }

    public string PostExitScriptPath
    {
        get => _postExitScriptPath;
        set
        {
            _postExitScriptPath = value;
            OnPropertyChanged();
            ClearFieldError(EditField.PostExitScript);
            OnPropertyChanged(nameof(HasPostExitScript));
            OnPropertyChanged(nameof(HasAnyScript));
            OnPropertyChanged(nameof(CanEditScriptArguments));
            OnPropertyChanged(nameof(DefaultScriptsSummary));
        }
    }

    /// <summary>Gates the "wait for pre-launch script" option, which only means something with a pre-launch script set.</summary>
    public bool HasPreLaunchScript => !string.IsNullOrWhiteSpace(_preLaunchScriptPath);

    public bool HasPostExitScript => !string.IsNullOrWhiteSpace(_postExitScriptPath);

    /// <summary>
    /// "Use the same script for pre-launch and post-exit": for one-file scripts that branch on the
    /// phase, like the bundled examples. While on, the post-exit path mirrors the pre-launch path
    /// and its box is read-only; turning it off leaves the copied path in place to edit.
    /// </summary>
    public bool UseSameScriptForBoth
    {
        get => _useSameScriptForBoth;
        set
        {
            if (_useSameScriptForBoth == value) return;
            _useSameScriptForBoth = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanEditPostExitScript));
            if (value) PostExitScriptPath = PreLaunchScriptPath;
        }
    }

    public bool CanEditPostExitScript => !_useSameScriptForBoth;
}
