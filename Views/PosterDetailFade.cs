using System.Windows;
using System.Windows.Data;
using TrayTrigger.Converters;

namespace TrayTrigger.Views;

/// <summary>
/// The parts of a poster card that "Show details on hover" can hold back until the card is in use.
/// Each name (bar <see cref="None"/>) is a bool property of <see cref="ViewModels.PosterDetailsViewModel"/>
/// saying whether that part stays on the card at rest.
/// </summary>
public enum PosterDetailPart
{
    None,
    LauncherLogo,
    Category,
    HiddenTag,
    PlayingTag,
    FavoriteStar,
    NotInstalledTag,
    Title,
    Playtime,
    LastPlayed,
    TopShade,
    BottomShade,
}

/// <summary>
/// Set on an element of the poster card template (<c>views:PosterDetailFade.Part="Title"</c>) to
/// bind its Opacity: a steady 1 when Settings keeps that part on the card at rest, otherwise the
/// card's details opacity - the template's <c>DetailsOpacity</c> element, which fades in and out as
/// the card comes into use. With the feature off that opacity is always 1. One attribute per element
/// rather than the same three-part MultiBinding written out for each of them.
/// </summary>
public static class PosterDetailFade
{
    private static readonly PickByFlagConverter Pick = new();

    public static readonly DependencyProperty PartProperty = DependencyProperty.RegisterAttached(
        "Part", typeof(PosterDetailPart), typeof(PosterDetailFade),
        new PropertyMetadata(PosterDetailPart.None, OnPartChanged));

    public static PosterDetailPart GetPart(DependencyObject element) => (PosterDetailPart)element.GetValue(PartProperty);

    public static void SetPart(DependencyObject element, PosterDetailPart value) => element.SetValue(PartProperty, value);

    private static void OnPartChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        if (e.NewValue is not PosterDetailPart part || part == PosterDetailPart.None)
        {
            BindingOperations.ClearBinding(element, UIElement.OpacityProperty);
            return;
        }

        var opacity = new MultiBinding { Converter = Pick };
        opacity.Bindings.Add(new Binding($"DataContext.SettingsVM.PosterDetails.{part}")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Window), 1),
        });
        opacity.Bindings.Add(new Binding { Source = 1.0 });
        opacity.Bindings.Add(new Binding(nameof(UIElement.Opacity)) { ElementName = "DetailsOpacity" });
        BindingOperations.SetBinding(element, UIElement.OpacityProperty, opacity);
    }
}
