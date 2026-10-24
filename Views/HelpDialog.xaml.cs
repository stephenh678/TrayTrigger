using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using TrayTrigger.Services;
using TrayTrigger.Services.Theming;

namespace TrayTrigger.Views;

/// <summary>
/// Non-modal "Learn more" window. One instance is reused: opening a second topic while the
/// window is up just swaps its content and brings it forward, so the user can keep clicking
/// links in the main window and read alongside the controls they describe.
/// </summary>
public partial class HelpDialog : Window
{
    private static HelpDialog? _current;

    private HelpDialog()
    {
        InitializeComponent();
        WindowThemeService.PrepareForFirstShow(this);
        Closed += (_, _) =>
        {
            PropertyChangedEventManager.RemoveHandler(ThemeState.Instance, OnThemeVersionChanged, nameof(ThemeState.Version));
            if (ReferenceEquals(_current, this)) _current = null;
        };
        // The text colours are looked up as the page is built, so a theme change while the window
        // is open would leave them as they were; build the page again.
        PropertyChangedEventManager.AddHandler(ThemeState.Instance, OnThemeVersionChanged, nameof(ThemeState.Version));
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    public static void ShowTopic(Window? owner, string topicId)
    {
        var topic = HelpContentService.GetTopic(topicId);

        if (_current == null)
        {
            _current = new HelpDialog();
            if (owner != null && !ReferenceEquals(owner, _current))
            {
                _current.Owner = owner;
            }
            _current.Load(topic, topicId);
            _current.Show();
            WindowThemeService.CenterOverOwner(_current);
        }
        else
        {
            _current.Load(topic, topicId);
            if (_current.WindowState == WindowState.Minimized) _current.WindowState = WindowState.Normal;
            _current.Activate();
        }
    }

    private HelpTopic? _topic;
    private string _topicId = "";

    private void OnThemeVersionChanged(object? sender, PropertyChangedEventArgs e) => Load(_topic, _topicId);

    private void Load(HelpTopic? topic, string topicId)
    {
        _topic = topic;
        _topicId = topicId;
        BodyPanel.Children.Clear();

        if (topic == null)
        {
            Title = "TrayTrigger Help";
            BreadcrumbText.Text = "TRAYTRIGGER HELP";
            TitleText.Text = "Topic not available";
            BodyPanel.Children.Add(MakeParagraph($"No help is bundled for \"{topicId}\" in this build."));
            return;
        }

        Title = $"{topic.Title} - TrayTrigger Help";
        BreadcrumbText.Text = "TRAYTRIGGER HELP  ·  " + HelpContentService.SectionLabel(topic.Id).ToUpperInvariant();
        TitleText.Text = topic.Title;

        bool first = true;
        foreach (var block in topic.Blocks)
        {
            UIElement element = block.Kind switch
            {
                HelpBlockKind.Heading => MakeHeading(block.Text, first),
                HelpBlockKind.Bullet => MakeBullet(block.Text, block.Indent),
                HelpBlockKind.Note => MakeNote(block.Text),
                _ => MakeParagraph(block.Text)
            };
            BodyPanel.Children.Add(element);
            first = false;
        }
    }

    // --- Block factories. Colors/fonts come from App.xaml resources so the dialog tracks the theme. ---

    private Brush Res(string key) => (Brush)FindResource(key);

    /// <summary>A piece of code in a line of help, in the app's monospace font (a live reference, like the sizes).</summary>
    private Run CodeRun(string text)
    {
        var run = new Run(text) { Foreground = Res("BrushTextPrimary") };
        run.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
        return run;
    }

    /// <summary>
    /// The block at the body text size, as a live resource: this window stays open beside Settings, so the page has to follow
    /// the Text size slider while it is being moved, not keep the size it was built at.
    /// </summary>
    private static TextBlock Body(TextBlock block)
    {
        block.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeBody");
        return block;
    }

    private TextBlock MakeHeading(string text, bool isFirst) => Body(new TextBlock
    {
        Text = text,
        FontWeight = FontWeights.Bold,
        Foreground = Res("BrushAccentText"),
        Margin = new Thickness(0, isFirst ? 0 : 14, 0, 6)
    });

    private TextBlock MakeParagraph(string text)
    {
        var tb = Body(new TextBlock
        {
            LineHeight = 18,
            Foreground = Res("BrushTextSecondary"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        AddInlines(tb, text);
        return tb;
    }

    /// <summary>
    /// Renders the inline Markdown the topics use - **bold** and `code` - as Runs so the
    /// markers never show literally (they did, in profiles/hdr.md). Unbalanced markers are
    /// left as plain text.
    /// </summary>
    private void AddInlines(TextBlock target, string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            int bold = text.IndexOf("**", i, StringComparison.Ordinal);
            int code = text.IndexOf('`', i);
            int next = bold < 0 ? code : code < 0 ? bold : Math.Min(bold, code);
            if (next < 0)
            {
                target.Inlines.Add(new Run(text.Substring(i)));
                return;
            }
            if (next > i) target.Inlines.Add(new Run(text.Substring(i, next - i)));

            bool isBold = next == bold;
            int markerLen = isBold ? 2 : 1;
            int end = isBold
                ? text.IndexOf("**", next + 2, StringComparison.Ordinal)
                : text.IndexOf('`', next + 1);
            if (end < 0)
            {
                target.Inlines.Add(new Run(text.Substring(next)));
                return;
            }

            string inner = text.Substring(next + markerLen, end - next - markerLen);
            target.Inlines.Add(isBold
                ? new Run(inner) { FontWeight = FontWeights.SemiBold, Foreground = Res("BrushTextPrimary") }
                : CodeRun(inner));
            i = end + markerLen;
        }
    }

    private UIElement MakeBullet(string text, int indent = 0)
    {
        var grid = new Grid { Margin = new Thickness(6 + indent * 18, 0, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = Body(new TextBlock
        {
            Text = indent == 0 ? "•" : "◦",
            Foreground = Res("BrushAccentText"),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Top
        });
        Grid.SetColumn(dot, 0);

        var body = Body(new TextBlock
        {
            LineHeight = 18,
            Foreground = Res("BrushTextSecondary"),
            TextWrapping = TextWrapping.Wrap
        });
        AddInlines(body, text);
        Grid.SetColumn(body, 1);

        grid.Children.Add(dot);
        grid.Children.Add(body);
        return grid;
    }

    private UIElement MakeNote(string text)
    {
        // The same note the settings pages use (the tip kind), on the Help window's card color.
        var note = new NoteBar { Kind = NoteKind.Tip, Margin = new Thickness(0, 2, 0, 10) };
        note.SetResourceReference(Control.BackgroundProperty, "BrushDialogCard");
        AddInlines(note.MessageBlock, text);
        return note;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
