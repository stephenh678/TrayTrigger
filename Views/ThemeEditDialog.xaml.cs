using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using TrayTrigger.Services;
using TrayTrigger.Services.Theming;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

/// <summary>
/// The Edit Theme window. The colors being edited show only in the preview card, which has its own set of colors
/// (filled in as they change); the rest of the app, this window included, keeps the theme that is on screen until Save.
/// </summary>
public partial class ThemeEditDialog : Window
{
    private readonly ThemeEditorViewModel _viewModel;

    /// <summary>How far the window grows to the right when the live preview opens; the editor keeps the width it has.</summary>
    private const double PreviewWidth = 680;


    internal ThemeEditDialog(ThemeEditorViewModel viewModel)
    {
        InitializeComponent();
        // Tall enough to show the editor without scrolling, but never taller than the screen has room for.
        Height = Math.Min(792, SystemParameters.WorkArea.Height - 40);
        WindowThemeService.PrepareForFirstShow(this);
        WindowHelper.RemoveMinimizeAndMaximize(this);
        _viewModel = viewModel;
        DataContext = viewModel;

        Owner = WindowHelper.ActiveOwner();

        // The preview's own colors: made once, then the changed ones are put in as the person edits.
        var previewColors = ThemeService.Build(viewModel.Palette);
        PreviewHost.Resources.MergedDictionaries.Add(previewColors);
        viewModel.PaletteChanged += (_, _) => ThemeService.Fill(previewColors, viewModel.Palette);

        // And its own corners, the same way: the shapes show in the preview only, until Save.
        var previewShapes = ThemeShape.Build(viewModel.Corners.Values);
        PreviewHost.Resources.MergedDictionaries.Add(previewShapes);
        viewModel.ShapeChanged += (_, _) => ThemeShape.Fill(previewShapes, viewModel.Corners.Values);

        // And its own text sizes: the preview shows the theme's text size, the editor around it keeps the app's. (A size given
        // when the app was started, for a capture, is the size of everything, the preview too.)
        double PreviewScale() => AppTypography.OverrideScale ?? viewModel.EffectiveTextSize / 100.0;
        var previewText = new ResourceDictionary();
        AppTypography.Fill(previewText, PreviewScale());
        PreviewHost.Resources.MergedDictionaries.Add(previewText);
        viewModel.TextSizeChanged += (_, _) => AppTypography.Fill(previewText, PreviewScale());

        // And its own font: the preview shows the theme's font, the editor around it keeps the app's.
        var previewFont = new ResourceDictionary();
        string PreviewFont() => AppFonts.OverrideName ?? viewModel.EffectiveFont;
        AppFonts.Fill(previewFont, PreviewFont());
        PreviewHost.Resources.MergedDictionaries.Add(previewFont);
        PreviewHost.SetResourceReference(TextElement.FontFamilyProperty, "AppFont");
        viewModel.FontChanged += (_, _) => AppFonts.Fill(previewFont, PreviewFont());

        // The editor opens at the size it was last left at (the preview panel, when out, is not part of that).
        TrayTrigger.Services.WindowSizeMemory.Attach(this, "ThemeEdit", () => viewModel.IsPreviewOpen ? _previewAdded : 0);

        // The live preview is a panel on the right edge: opening it makes the window wider, and closing it takes that back.
        // It opens the way it was left (closed the first time, as the editor is the size it is meant to be seen at).
        viewModel.IsPreviewOpen = TrayTrigger.Services.WindowSizeMemory.ThemeEditPreviewOpen;
        Closed += (_, _) => TrayTrigger.Services.WindowSizeMemory.RememberThemeEditPreviewOpen(viewModel.IsPreviewOpen);
        ShowPreview(viewModel.IsPreviewOpen, firstTime: true);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ThemeEditorViewModel.IsPreviewOpen)) ShowPreview(viewModel.IsPreviewOpen, firstTime: false);
        };

        // Every way out but Save (Cancel, Esc, Alt+F4, the X) asks before throwing edits away. Esc reaches here through the
        // Cancel button (IsCancel), and an Esc a child already used (the color picker closing, a drop-down closing) never does.
        Closing += (s, e) =>
        {
            if (DialogResult == true || !viewModel.HasUnsavedChanges) return;
            // One click on Cancel can ask to close twice (the button's own close, then its command): answer the second the same.
            if (Environment.TickCount64 - _keptEditingAt < 500)
            {
                e.Cancel = true;
                return;
            }
            if (!ModernDialog.Confirm(this, "Discard Changes", "Discard your changes?",
                    "Nothing you changed here has been saved.", "Discard", "Keep Editing"))
            {
                e.Cancel = true;
                _keptEditingAt = Environment.TickCount64;
            }
        };

        Loaded += (s, e) =>
        {
            WindowThemeService.CenterOverOwner(this);
            Activate();
        };

        viewModel.RequestClose += success =>
        {
            // Saving closes through the result; cancelling closes plainly, once, so a "Keep Editing" is asked about once.
            if (success) DialogResult = true;
            else Close();
        };
    }

    /// <summary>The theme file the dialog saved; null when it was cancelled.</summary>
    internal string? SavedPath => _viewModel.SavedPath;

    /// <summary>
    /// Opens or closes the live preview panel. The window is made wider (or narrower) by the panel's width, so the editor
    /// is the size it was; the window moves left when it would run off the screen, and only a screen too narrow for both
    /// makes the editor give some width up.
    /// </summary>
    private void ShowPreview(bool open, bool firstTime)
    {
        PreviewPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        var work = SystemParameters.WorkArea;
        var (target, added) = PlanPreview(open, Width, _previewAdded, PreviewWidth, WorkWidthOverride ?? work.Width, MinWidth);
        if (!firstTime && open && !double.IsNaN(Left) && Left + target > work.Right) Left = Math.Max(work.Left, work.Right - target);
        _previewAdded = added;
        // The panel takes what the window gained, so on a narrow screen the preview is the narrower one, not the editor.
        PreviewColumn.Width = new GridLength(added);
        Width = target;
    }

    /// <summary>
    /// The window width with the preview out or in, and the width the panel really added. That is less than its own on a screen
    /// too narrow for both, so closing it takes back just that, the editor is the size it was, and what is kept for next time
    /// leaves out only that. It is never negative: a screen narrower than the editor itself (a small display at a high scale)
    /// gives the panel nothing, where a negative width would stop the editor from opening.
    /// </summary>
    internal static (double Target, double Added) PlanPreview(bool open, double width, double alreadyAdded, double previewWidth, double workWidth, double minWidth)
    {
        double target = open ? Math.Min(width + previewWidth, workWidth) : width - alreadyAdded;
        target = Math.Max(target, minWidth);
        return (target, open ? Math.Max(0, target - width) : 0);
    }

    /// <summary>Set by a screenshot mode to see the dialog as a screen of another width would show it; null in normal use.</summary>
    internal static double? WorkWidthOverride { get; set; }

    private double _previewAdded;
    private long _keptEditingAt = long.MinValue / 2;

    /// <summary>
    /// Runs before the Save command. A number typed in the text size box is taken when the box loses focus, and Enter (the default
    /// button) or Alt+S presses Save without moving the focus, so the number would be saved as it was before it was typed.
    /// </summary>
    private void SaveButton_Click(object sender, RoutedEventArgs e) =>
        (Keyboard.FocusedElement as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

    private void StartFromDark_Click(object sender, RoutedEventArgs e) => _viewModel.IsDark = true;

    private void StartFromLight_Click(object sender, RoutedEventArgs e) => _viewModel.IsLight = true;
}
