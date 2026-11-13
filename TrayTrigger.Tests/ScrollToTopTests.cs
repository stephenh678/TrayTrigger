using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TrayTrigger.ViewModels;
using TrayTrigger.Views;

namespace TrayTrigger.Tests;

public class ScrollToTopTests
{
    private sealed class Tabs : INotifyPropertyChanged
    {
        public GameEditSection Section { get; set; } = GameEditSection.All;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Refresh(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // Ticking a box on Edit Game's All tab threw the page back to the top: a refresh hands the binding a
    // freshly boxed All, which an object-typed property took for a change of tab.
    [Fact]
    public void ARefreshOnTheSameTab_KeepsTheScrollPosition_AndAChangeOfTabScrollsUp() => WpfTestHost.Run(() =>
    {
        var tabs = new Tabs();
        var scroller = new ScrollViewer { Content = new Border { Height = 2000 } };
        BindingOperations.SetBinding(scroller, ScrollToTop.ResetOnProperty, new Binding(nameof(Tabs.Section)) { Source = tabs });
        var window = new Window { Content = scroller, Width = 300, Height = 200, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -10000 };
        try
        {
            window.Show();
            scroller.ScrollToVerticalOffset(600);
            window.UpdateLayout();
            Assert.Equal(600, scroller.VerticalOffset);

            tabs.Refresh(nameof(Tabs.Section));
            tabs.Refresh(string.Empty);
            window.UpdateLayout();
            Assert.Equal(600, scroller.VerticalOffset);

            tabs.Section = GameEditSection.Launch;
            tabs.Refresh(nameof(Tabs.Section));
            window.UpdateLayout();
            Assert.Equal(0, scroller.VerticalOffset);
        }
        finally
        {
            window.Close();
        }
    });
}
