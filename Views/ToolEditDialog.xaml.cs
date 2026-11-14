using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

public partial class ToolEditDialog : Window
{
    /// <param name="showHotkey">Scrolls to the hotkey and outlines it (About's list of hotkeys opens the tool at it).</param>
    public ToolEditDialog(ToolEntry tool, IEnumerable<string> categories, IconExtractorService iconExtractorService, bool showHotkey = false)
    {
        InitializeComponent();
        TrayTrigger.Services.WindowSizeMemory.Attach(this, "ToolEdit");
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
            switch (ModernDialog.PromptSaveChanges(this, viewModel.Name))
            {
                // A save that closes goes through RequestClose; one a field refuses leaves the dialog open, saying why.
                case DialogResultOption.Primary: viewModel.SaveCommand.Execute(null); break;
                case DialogResultOption.Secondary: viewModel.CancelCommand.Execute(null); break;
            }
        };

        Loaded += (s, e) =>
        {
            WindowThemeService.CenterOverOwner(this);
            Activate();
            if (showHotkey) FieldHighlight.Show(ToolHotkeyRow);
        };

        viewModel.RequestClose += success =>
        {
            DialogResult = success;
            Close();
        };
    }
}
