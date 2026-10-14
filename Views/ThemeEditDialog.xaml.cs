using System;
using System.Windows;
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

        // The editor opens at the size it was last left at (the preview panel, when out, is not part of that).
        TrayTrigger.Services.WindowSizeMemory.Attach(this, "ThemeEdit", () => viewModel.IsPreviewOpen ? PreviewWidth : 0);

        // The live preview is a panel on the right edge: opening it makes the window wider, and closing it takes that back.
        // It starts closed, as the editor is the size it is meant to be seen at; the tab opens it.
        ShowPreview(viewModel.IsPreviewOpen, firstTime: true);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ThemeEditorViewModel.IsPreviewOpen)) ShowPreview(viewModel.IsPreviewOpen, firstTime: false);
        };

        // Esc cancels through the Cancel button (IsCancel), but not over unsaved edits. This listens on the bubbling
        // KeyDown, so an Esc a child already used (the color picker closing, a drop-down closing) never gets here.
        KeyDown += (s, e) =>
        {
            if (e.Key != Key.Escape || e.Handled || !viewModel.HasUnsavedChanges) return;
            e.Handled = true;
            if (ModernDialog.Confirm(this, "Discard Changes", "Discard your changes?",
                    "Nothing you changed here has been saved.", "Discard", "Keep Editing"))
            {
                viewModel.CancelCommand.Execute(null);
            }
        };

        Loaded += (s, e) =>
        {
            WindowThemeService.CenterOverOwner(this);
            Activate();
        };

        viewModel.RequestClose += success =>
        {
            DialogResult = success;
            Close();
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
        PreviewColumn.Width = new GridLength(open ? PreviewWidth : 0);
        PreviewPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        var work = SystemParameters.WorkArea;
        double target = firstTime ? Width + (open ? PreviewWidth : 0) : Width + (open ? PreviewWidth : -PreviewWidth);
        target = Math.Min(target, work.Width);
        if (!firstTime && open && !double.IsNaN(Left) && Left + target > work.Right) Left = Math.Max(work.Left, work.Right - target);
        Width = Math.Max(target, MinWidth);
    }

    private void StartFromDark_Click(object sender, RoutedEventArgs e) => _viewModel.IsDark = true;

    private void StartFromLight_Click(object sender, RoutedEventArgs e) => _viewModel.IsLight = true;
}
