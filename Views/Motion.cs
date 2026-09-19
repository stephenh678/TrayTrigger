using System;
using System.ComponentModel;
using System.Windows;

namespace TrayTrigger.Views;

/// <summary>
/// Whether decorative motion is wanted: Windows' Settings › Accessibility › Visual effects ›
/// Animation effects (SystemParameters.ClientAreaAnimation). Off means the poster hover zoom and
/// the launch popup's fade are skipped; the state they lead to still happens, only the transition
/// goes. Progress indicators (the System page spinner, the launch popup's sweep) keep moving, as
/// Windows' own do, because they say something is still working.
///
/// Bind in XAML as <c>{Binding Path=(views:Motion.IsEnabled)}</c>; it follows a change made
/// while TrayTrigger runs.
/// </summary>
public static class Motion
{
    private static bool _isEnabled = ReadSystemSetting();

    static Motion()
    {
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) IsEnabled = ReadSystemSetting();
        };
    }

    public static event EventHandler<PropertyChangedEventArgs>? StaticPropertyChanged;

    public static bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }

    private static bool ReadSystemSetting()
    {
        try { return SystemParameters.ClientAreaAnimation; }
        catch { return true; }
    }
}
