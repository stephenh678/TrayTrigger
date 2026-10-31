using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

public partial class GameDetailsDialog : Window
{
    private readonly GameDetailsViewModel _viewModel;
    private readonly double _defaultWidth;
    private readonly double _defaultHeight;

    public GameDetailsDialog(GameDetailsViewModel viewModel)
    {
        InitializeComponent();
        _defaultWidth = Width;
        _defaultHeight = Height;
        // Taller than a small screen's work area: fit it, rather than push the buttons off the bottom.
        var work = SystemParameters.WorkArea;
        if (Height > work.Height) Height = Math.Max(MinHeight, work.Height - 24);
        if (Width > work.Width) Width = Math.Max(MinWidth, work.Width - 24);
        _viewModel = viewModel;
        WindowThemeService.PrepareForFirstShow(this);
        WindowHelper.RemoveMinimizeAndMaximize(this);
        DataContext = viewModel;

        viewModel.RequestClose += () =>
        {
            Dispatcher.Invoke(Close);
        };

        if (WindowHelper.ActiveOwner() is { } main && main != this)
        {
            Owner = main;
        }

        // Play, Remove and Close Game ask before they close the page over edits; so does closing it.
        viewModel.ConfirmLeave = ConfirmLeave;
        Closing += (s, e) =>
        {
            if (!ConfirmLeave()) e.Cancel = true;
        };

        Loaded += (s, e) =>
        {
            WindowThemeService.CenterOverOwner(this);
            Activate();
            _ = _viewModel.LoadInstallDriveAsync();
        };
    }

    private bool _discardConfirmed;

    /// <summary>
    /// True when nothing in Settings is open to lose, or when the player says to discard it. Asked once:
    /// after a yes, the rest of the closing goes through without asking again.
    /// </summary>
    private bool ConfirmLeave()
    {
        // While TrayTrigger is shutting down nobody can be asked, and the close goes ahead whatever the answer.
        if (_discardConfirmed || !_viewModel.HasUnsavedEdits || Dispatcher.HasShutdownStarted) return true;
        if (!ModernDialog.Confirm(this, "Discard Changes", "Discard your changes?",
                "Nothing you changed in Settings has been saved.", "Discard", "Keep Editing"))
            return false;
        _discardConfirmed = true;
        return true;
    }

    /// <summary>The game's banner has been found and is on show. Diagnostics wait for it before they capture.</summary>
    internal bool HasHeroImage => _viewModel.HasHeroImage;

    /// <summary>The Settings tab's editor, once the tab has been opened. Diagnostics drive it directly.</summary>
    internal GameEditViewModel? EditViewModel => _viewModel.EditViewModel;

    /// <summary>Opens the Settings tab, on one of its own tabs when given: what Edit and the card menu's Edit do.</summary>
    public void ShowSettings(GameEditSection? section = null) => _viewModel.ShowSettings(section);

    /// <summary>Opens the menu a button carries (the ... menu, the info-source menu) beneath it.</summary>
    private void OpenMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    /// <summary>
    /// Use the size the player last left this window at, in place of the XAML default. Ignored
    /// when it is missing, below the window's minimum, or larger than the work area, so a size
    /// saved on a bigger display can't come back too big to reach its buttons.
    /// </summary>
    public void ApplySavedSize(double? width, double? height)
    {
        if (width is not double w || height is not double h || double.IsNaN(w) || double.IsNaN(h)) return;
        var workArea = SystemParameters.WorkArea;
        if (w < MinWidth || h < MinHeight || w > workArea.Width || h > workArea.Height) return;
        Width = w;
        Height = h;
    }

    /// <summary>
    /// The size to remember, read once the window has closed; null when it is at the XAML default,
    /// so a player who never resized it keeps following the default if that is ever changed.
    /// </summary>
    public (double Width, double Height)? SizeToRemember
    {
        get
        {
            double width = Math.Round(ActualWidth), height = Math.Round(ActualHeight);
            if (width <= 0 || height <= 0) return null;
            return width == _defaultWidth && height == _defaultHeight ? null : (width, height);
        }
    }

    /// <summary>
    /// Drop straight into the match picker once the dialog's first load settles. Used by the
    /// library card's "Change Match..." item, which opens this dialog only to reach the picker -
    /// having to click Change match again on arrival would defeat the point.
    ///
    /// It has to wait: ChangeMatchCommand is gated on CanChangeMatch, which is false while the
    /// details are still on their way in, so firing it at Loaded would silently do nothing.
    /// </summary>
    public void OpenMatchPickerWhenReady()
    {
        if (_viewModel.CanChangeMatch)
        {
            QueueMatchPicker();
            return;
        }

        void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(GameDetailsViewModel.CanChangeMatch) || !_viewModel.CanChangeMatch)
                return;

            _viewModel.PropertyChanged -= OnPropertyChanged;
            QueueMatchPicker();
        }

        _viewModel.PropertyChanged += OnPropertyChanged;

        // The picker is modal, so it must not open from inside the load-completion callback that
        // raised CanChangeMatch - that would block the rest of that method behind the dialog.
        Closed += (s, e) => _viewModel.PropertyChanged -= OnPropertyChanged;
    }

    private void QueueMatchPicker() => Dispatcher.BeginInvoke(
        DispatcherPriority.Background,
        () =>
        {
            if (IsLoaded && _viewModel.ChangeMatchCommand.CanExecute(null))
                _viewModel.ChangeMatchCommand.Execute(null);
        });

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
