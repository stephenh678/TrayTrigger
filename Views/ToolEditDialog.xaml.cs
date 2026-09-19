using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

public partial class ToolEditDialog : Window
{
    public ToolEditDialog(ToolEntry tool, IEnumerable<string> categories, IconExtractorService iconExtractorService)
    {
        InitializeComponent();
        WindowThemeService.PrepareForFirstShow(this);
        WindowHelper.RemoveMinimizeAndMaximize(this);
        var viewModel = new ToolEditViewModel(tool, categories, iconExtractorService);
        DataContext = viewModel;

        Owner = WindowHelper.ActiveOwner();

        // Esc cancels through the Cancel button (IsCancel), but not over unsaved edits. This
        // listens on the bubbling KeyDown, so an Esc a child already used - the hotkey recorder
        // while it is recording, a combo box closing its drop-down - never gets here.
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
}
