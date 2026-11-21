using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

public partial class ToolEditDialog : Window
{
    /// <param name="showHotkey">Scrolls to the hotkey and outlines it (About's list of hotkeys opens the tool at it).</param>
    /// <param name="collections">The library's collections, for "Limit to a collection".</param>
    public ToolEditDialog(ToolEntry tool, IEnumerable<string> categories, IconExtractorService iconExtractorService, bool showHotkey = false,
        IEnumerable<string>? collections = null)
    {
        InitializeComponent();
        TrayTrigger.Services.WindowSizeMemory.Attach(this, "ToolEdit");
        WindowThemeService.PrepareForFirstShow(this);
        WindowHelper.RemoveMinimizeAndMaximize(this);
        var viewModel = new ToolEditViewModel(tool, categories, iconExtractorService);
        viewModel.KnownCollections = (collections ?? Array.Empty<string>()).ToList();
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

    private void ChooseCollections_Click(object sender, RoutedEventArgs e)
    {
        var vm = (ToolEditViewModel)DataContext;
        // The library's collections, plus any this tool names that no game is in now (one deleted, or renamed by hand).
        var names = vm.KnownCollections.Concat(vm.OnlyForCollections)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var choices = names.Select(n => new CollectionChoice(n, vm.OnlyForCollections.Any(o => string.Equals(o, n, StringComparison.OrdinalIgnoreCase)))).ToList();
        var dialog = new CollectionsDialog("Collections", "Limit this tool to games in the collections you add. Remove them all and it applies to every game.",
            choices, validateNew: null,
            emptyText: "No collections yet. Put games in collections from the Library's right-click menu, then come back.",
            noneChosenText: "No limit: this tool applies to every game.");
        dialog.Owner = this;
        if (dialog.ShowDialog() == true) vm.OnlyForCollections = dialog.Ticked;
    }
}
