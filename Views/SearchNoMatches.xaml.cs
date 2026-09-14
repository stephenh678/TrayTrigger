using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace TrayTrigger.Views;

/// <summary>
/// The "No matches" message under a page's tabs. <see cref="Host"/> is the panel carrying that page's
/// <see cref="CardSearch"/>; the message shows exactly while its NoMatches is set.
/// </summary>
public partial class SearchNoMatches : UserControl
{
    public static readonly DependencyProperty HostProperty = DependencyProperty.Register(
        nameof(Host), typeof(UIElement), typeof(SearchNoMatches),
        new PropertyMetadata(null, (d, e) => ((SearchNoMatches)d).FollowHost(e.NewValue as UIElement)));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SearchNoMatches), new PropertyMetadata("No matches"));

    public UIElement? Host
    {
        get => (UIElement?)GetValue(HostProperty);
        set => SetValue(HostProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public SearchNoMatches()
    {
        InitializeComponent();
        TitleText.SetBinding(TextBlock.TextProperty, new Binding(nameof(Title)) { Source = this });
    }

    private void FollowHost(UIElement? host)
    {
        if (host == null)
        {
            BindingOperations.ClearBinding(this, VisibilityProperty);
            Visibility = Visibility.Collapsed;
            return;
        }
        SetBinding(VisibilityProperty, new Binding
        {
            Source = host,
            Path = new PropertyPath(CardSearch.NoMatchesProperty),
            Converter = new BooleanToVisibilityConverter(),
        });
    }
}
