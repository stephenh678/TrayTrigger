using System.Collections.Generic;
using System.Windows;
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
