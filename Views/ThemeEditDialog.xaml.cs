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

    internal ThemeEditDialog(ThemeEditorViewModel viewModel)
    {
        InitializeComponent();
        // Tall enough to show the preview with its states without scrolling, but never taller than the screen has room for.
        Height = Math.Min(830, SystemParameters.WorkArea.Height - 40);
        WindowThemeService.PrepareForFirstShow(this);
        WindowHelper.RemoveMinimizeAndMaximize(this);
        _viewModel = viewModel;
        DataContext = viewModel;

        Owner = WindowHelper.ActiveOwner();

        // The preview's own colors: made once, then the changed ones are put in as the person edits.
        var previewColors = ThemeService.Build(viewModel.Palette);
        PreviewHost.Resources.MergedDictionaries.Add(previewColors);
        viewModel.PaletteChanged += (_, _) => ThemeService.Fill(previewColors, viewModel.Palette);

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

    private void StartFromDark_Click(object sender, RoutedEventArgs e) => _viewModel.IsDark = true;

    private void StartFromLight_Click(object sender, RoutedEventArgs e) => _viewModel.IsLight = true;
}
