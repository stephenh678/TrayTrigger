using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using Xunit;
using static TrayTrigger.Tests.SourceTree;

namespace TrayTrigger.Tests;

/// <summary>
/// A plain Trigger on Tag never fires for a bound bool: Tag is an object, so the trigger's "True" stays a
/// string and is never equal to the boxed bool the binding puts there. A DataTrigger converts the value to the
/// binding's type and does fire - but among a ControlTemplate's own triggers the control is
/// <c>RelativeSource Self</c>, and TemplatedParent finds nothing. The saved-filter rows got both wrong in turn
/// and never showed as selected.
/// </summary>
public class TriggerGuardTests
{
    [Fact]
    public void NoXaml_UsesAPlainTriggerOnTag()
    {
        // Across lines: a tag's attributes are often on the next line down.
        var plainTriggerOnTag = new Regex("<Trigger\\b[^<>]*?\\sProperty=\"Tag\"", RegexOptions.Singleline);
        var offenders = new List<string>();
        foreach (var file in Sources("*.xaml"))
        {
            string text = Read(file);
            foreach (Match m in plainTriggerOnTag.Matches(text))
                offenders.Add($"{file}:{text.Take(m.Index).Count(c => c == '\n') + 1}  {m.Value.Split('\n')[0].Trim()}");
        }

        Assert.True(offenders.Count == 0,
            "Use a DataTrigger bound to Tag with RelativeSource Self. Found:\n" + string.Join('\n', offenders));
    }

    /// <summary>
    /// Puts the named resources on the shared test Application for the length of <paramref name="body"/>, and puts
    /// back what was there before - not just removes them: another test may have its own on the same keys.
    /// </summary>
    private static void WithAppResources(IReadOnlyDictionary<string, object> keys, Action body)
    {
        var resources = Application.Current.Resources;
        var before = keys.Keys.ToDictionary(k => k, k => resources.Contains(k) ? resources[k] : null);
        foreach (var (key, value) in keys) resources[key] = value;
        try { body(); }
        finally
        {
            foreach (var (key, previous) in before)
            {
                if (previous is null) resources.Remove(key);
                else resources[key] = previous;
            }
        }
    }

    /// <summary>The resources the filter flyout's check styles read, as stand-ins.</summary>
    private static Dictionary<string, object> CheckStyleResources(SolidColorBrush accentText, SolidColorBrush warning) => new()
    {
        ["AppFont"] = new FontFamily("Segoe UI"),
        ["FontSizeBody"] = 12.0,
        ["RadiusSmall"] = new CornerRadius(4),
        ["BrushTextPrimary"] = new SolidColorBrush(Colors.White),
        ["BrushSurfaceHover"] = new SolidColorBrush(Colors.Gray),
        ["BrushAccentText"] = accentText,
        ["BrushWarning"] = warning,
        ["KeyboardFocusRing"] = new Style(),
    };

    /// <summary>One style out of App.xaml, parsed on its own.</summary>
    private static Style StyleFromApp(string key)
    {
        string app = Read("App.xaml");
        int start = app.IndexOf($"<Style x:Key=\"{key}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{key} is gone from App.xaml.");
        string style = app[start..(app.IndexOf("</Style>", start, StringComparison.Ordinal) + "</Style>".Length)]
            .Replace("<Style ", "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" ");
        return (Style)XamlReader.Parse(style);
    }

    private static Visibility TickOf(Control b) => ((System.Windows.Shapes.Path)b.Template.FindName("Tick", b)).Visibility;

    /// <summary>
    /// The real FilterOptionToggle style: a ticked option and its opposite both show the same blue check, so the
    /// row never needs a fill to say it is on, and an unticked one draws nothing; the opposite also turns the
    /// label the warning color.
    /// </summary>
    [Fact]
    public void FilterOption_ShowsTheBlueCheck_ForTickedAndForTheOpposite() => WpfTestHost.Run(() =>
    {
        var warning = new SolidColorBrush(Colors.Orange);
        var accentText = new SolidColorBrush(Colors.Blue);
        WithAppResources(CheckStyleResources(accentText, warning), () =>
        {
            var style = StyleFromApp("FilterOptionToggle");
            var off = new ToggleButton { Style = style, IsThreeState = true, IsChecked = false, Content = "x" };
            var on = new ToggleButton { Style = style, IsThreeState = true, IsChecked = true, Content = "x" };
            var opposite = new ToggleButton { Style = style, IsThreeState = true, IsChecked = null, Content = "x" };
            foreach (var toggle in new[] { off, on, opposite }) toggle.ApplyTemplate();
            var results = (TickOf(off), TickOf(on), TickOf(opposite), on.Foreground, opposite.Foreground,
                ((System.Windows.Shapes.Path)opposite.Template.FindName("Tick", opposite)).Stroke);

            Assert.Equal(Visibility.Collapsed, results.Item1);
            Assert.Equal(Visibility.Visible, results.Item2);
            Assert.Equal(Visibility.Visible, results.Item3);
            Assert.NotSame(warning, results.Item4);
            Assert.Same(warning, results.Item5);
            // The check stays blue in the opposite state: only the label changes color.
            Assert.Same(accentText, results.Item6);
        });
    });

    /// <summary>The real FilterSavedRow style, taken from App.xaml, drawn with its two states.</summary>
    [Fact]
    public void SavedFilterRow_IsChecked_OnlyWhenItsTagIsTrue() => WpfTestHost.Run(() =>
    {
        WithAppResources(CheckStyleResources(new SolidColorBrush(Colors.Blue), new SolidColorBrush(Colors.Orange)), () =>
        {
            var style = StyleFromApp("FilterSavedRow");
            var selected = new Button { Style = style, Tag = true, Content = "x" };
            var other = new Button { Style = style, Tag = false, Content = "x" };
            selected.ApplyTemplate();
            other.ApplyTemplate();

            var results = (TickOf(selected), TickOf(other),
                System.Windows.Automation.AutomationProperties.GetItemStatus(selected),
                System.Windows.Automation.AutomationProperties.GetItemStatus(other));

            Assert.Equal(Visibility.Visible, results.Item1);
            Assert.Equal(Visibility.Collapsed, results.Item2);
            // A screen reader hears which one is applied, as it does for a selected tab.
            Assert.Equal("applied", results.Item3);
            Assert.Equal(string.Empty, results.Item4);
        });
    });
}
