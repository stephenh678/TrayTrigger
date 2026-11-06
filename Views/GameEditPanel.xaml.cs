using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

/// <summary>
/// The settings of one game, hosted on Game Details' Settings tab. The view model is replaced after a
/// Save or a Cancel (the new one starts clean from the game as it is), so what hangs off it is
/// rewired whenever the data context changes.
/// </summary>
public partial class GameEditPanel : UserControl
{
    private GameEditViewModel? _viewModel;

    public GameEditPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as GameEditViewModel);

        // Ctrl+Tab / Ctrl+Shift+Tab step through the tabs, as in a browser. A bubbling KeyDown, so a
        // recording hotkey box still gets the combination first.
        KeyDown += (_, e) =>
        {
            if (e.Handled || _viewModel == null) return;
            if (e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                _viewModel.MoveSection(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                e.Handled = true;
            }
        };

        // A field a link asked for is shown once the panel is on screen and laid out.
        Loaded += (_, _) => ShowRequestedField();
        IsVisibleChanged += (_, _) => ShowRequestedField();
    }

    private void OnFieldRequested() => ShowRequestedField();

    private FrameworkElement? TargetOf(GameEditViewModel.EditField field) => field switch
    {
        GameEditViewModel.EditField.LaunchHotkey => LaunchHotkeyRow,
        GameEditViewModel.EditField.RunOptionsCard => RunOptionsCard,
        GameEditViewModel.EditField.ProfileCard => ProfileCard,
        GameEditViewModel.EditField.DlssCard => DlssCard,
        GameEditViewModel.EditField.DisplayCard => DisplayCard,
        GameEditViewModel.EditField.ScriptsCard => ScriptsCard,
        _ => null,
    };

    /// <summary>
    /// The setting a link pointed at (About's hotkey list, Game Details' "Your setup"): scrolled to and outlined
    /// once it is on screen. A tab that has only just changed may not have shown the card yet, so it looks again
    /// after the next layout, a few times, before giving up on a card this PC doesn't show (DLSS without an NVIDIA card).
    /// </summary>
    private void ShowRequestedField(int attempt = 0)
    {
        if (_viewModel?.PendingField is not { } field || !IsVisible) return;
        var target = TargetOf(field);
        if (target is not { IsVisible: true })
        {
            if (attempt < 3) Dispatcher.BeginInvoke(() => ShowRequestedField(attempt + 1), DispatcherPriority.Loaded);
            else _viewModel.TakeFieldToShow();
            return;
        }
        _viewModel.TakeFieldToShow();
        FieldHighlight.Show(target);
    }

    // --- A picture dropped on an Artwork card ---

    private static string? DroppedFile(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files ? files[0] : null;

    private static ArtSlot SlotOf(object sender) =>
        sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out ArtSlot slot) ? slot : ArtSlot.Poster;

    private void OnArtDragEnter(object sender, DragEventArgs e)
    {
        if (DroppedFile(e) != null && sender is Border card) card.SetResourceReference(Border.BorderBrushProperty, "BrushAccent");
    }

    private void OnArtDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedFile(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnArtDragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border card) card.SetResourceReference(Border.BorderBrushProperty, "BrushBorder");
    }

    private void OnArtDrop(object sender, DragEventArgs e)
    {
        OnArtDragLeave(sender, e);
        if (DroppedFile(e) is { } file) _viewModel?.TryUseImage(SlotOf(sender), file);
        e.Handled = true;
    }

    private void Attach(GameEditViewModel? viewModel)
    {
        if (_viewModel != null)
        {
            _viewModel.ValidationFailed -= OnValidationFailed;
            _viewModel.ScriptTestCompleted -= OnScriptTestCompleted;
            _viewModel.FieldRequested -= OnFieldRequested;
        }
        _viewModel = viewModel;
        if (viewModel == null) return;

        viewModel.ValidationFailed += OnValidationFailed;
        viewModel.ScriptTestCompleted += OnScriptTestCompleted;
        viewModel.FieldRequested += OnFieldRequested;
        Dispatcher.BeginInvoke(() => ShowRequestedField(), DispatcherPriority.Loaded);
        // The DLSS probe reads the driver and the game folder, so it runs here rather than in the view
        // model's constructor: the page opens at once and the card appears when there is something
        // true to put in it.
        _ = viewModel.Dlss.LoadAsync();
    }

    /// <summary>Save refused a value: the view model has already switched to a tab that shows it, so
    /// scroll the field into view and put the cursor in it. The reason is under the field.</summary>
    private void OnValidationFailed(GameEditViewModel.EditField field)
    {
        var box = field switch
        {
            GameEditViewModel.EditField.SteamAppId => SteamAppIdBox,
            GameEditViewModel.EditField.PreLaunchScript => PreLaunchScriptBox,
            GameEditViewModel.EditField.PostExitScript => PostExitScriptBox,
            GameEditViewModel.EditField.CpuCoresDelay => CpuCoresDelayBox,
            _ => PreLaunchTimeoutBox,
        };
        Dispatcher.BeginInvoke(() =>
        {
            box.BringIntoView();
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    private void OnScriptTestCompleted(ScriptTestReport report)
    {
        var dialog = new ScriptTestResultDialog(report) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }
}
