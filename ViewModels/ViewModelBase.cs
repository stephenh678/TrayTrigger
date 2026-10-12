using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using TrayTrigger.Services.Theming;

namespace TrayTrigger.ViewModels;

/// <summary>
/// Abstract base class for all ViewModels, providing standardized INotifyPropertyChanged
/// implementation and property change notification helpers.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Call from the constructor of a view model that hands the view a color it worked out in code (a badge
    /// color as a string, a brush picked by state). <see cref="OnThemeChanged"/> then runs whenever the theme
    /// changes, so it can raise those properties again. The listener holds this object weakly.
    /// </summary>
    protected void ListenForThemeChanges() =>
        PropertyChangedEventManager.AddHandler(ThemeState.Instance, OnThemeVersionChanged, nameof(ThemeState.Version));

    private void OnThemeVersionChanged(object? sender, PropertyChangedEventArgs e) => OnThemeChanged();

    /// <summary>Raise the color properties again. The default does nothing.</summary>
    protected virtual void OnThemeChanged() { }

    protected virtual bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
