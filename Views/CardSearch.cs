using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TrayTrigger.Views;

/// <summary>One card a search found: where it lives, what to call it, and the line that matched.</summary>
internal sealed record CardHit(UIElement Card, Panel Host, object? Tab, string TabLabel, string Title, string Snippet);

/// <summary>
/// Card-level search for a sectioned page (Settings, About, System). <c>Query</c> goes on the panel
/// inside the page's ScrollViewer: each Panel child of that host is a section, the section child
/// marked <c>IsHeader</c> is its heading, and every other section child is a card. A section names its
/// tab with <c>Tab</c> and <c>TabLabel</c>.
/// <para>
/// The page never shrinks to the matches. <see cref="Find"/> reads every card on every tab - they are
/// only collapsed, so their text is still there - and the page's <see cref="SearchBox"/> lists the hits
/// in a dropdown. Choosing one runs the host's <c>GoTo</c> command with the card's tab, scrolls the card
/// into view and outlines it (<see cref="GoTo"/>). The query's words are marked inside the chosen row
/// (<see cref="SearchHighlight"/>); <c>Scope</c> re-runs that when the tab changes.
/// </para>
/// A card matches when every word of the query appears in its text - titles, descriptions, option
/// labels, tooltips - or its tab's name. What's typed into a TextBox is never read, so an API key
/// can't match.
/// </summary>
public static class CardSearch
{
    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query", typeof(string), typeof(CardSearch),
        new PropertyMetadata(null, (d, e) =>
        {
            if (d is not Panel host) return;
            // A new query ends the outline and the marked words of the last result.
            ClearOutline(host);
            host.ClearValue(HighlightRootProperty);
            Apply(host, e.NewValue as string);
        }));

    public static string? GetQuery(DependencyObject d) => (string?)d.GetValue(QueryProperty);
    public static void SetQuery(DependencyObject d, string? value) => d.SetValue(QueryProperty, value);

    /// <summary>
    /// What's on screen: the selected tab, plus anything else that adds, removes or rewrites cards. A
    /// change re-runs the highlight against what's there now, leaving the scroll position alone -
    /// deferred and coalesced, because this binding can update before the page's own visibility
    /// bindings do.
    /// </summary>
    public static readonly DependencyProperty ScopeProperty = DependencyProperty.RegisterAttached(
        "Scope", typeof(object), typeof(CardSearch),
        new PropertyMetadata(null, (d, _) => { if (d is Panel host) ScheduleReapply(host); }));

    public static object? GetScope(DependencyObject d) => d.GetValue(ScopeProperty);
    public static void SetScope(DependencyObject d, object? value) => d.SetValue(ScopeProperty, value);

    public static readonly DependencyProperty IsHeaderProperty = DependencyProperty.RegisterAttached(
        "IsHeader", typeof(bool), typeof(CardSearch), new PropertyMetadata(false));

    public static bool GetIsHeader(DependencyObject d) => (bool)d.GetValue(IsHeaderProperty);
    public static void SetIsHeader(DependencyObject d, bool value) => d.SetValue(IsHeaderProperty, value);

    /// <summary>On a section: the page's own name for the tab it belongs to (what the tab command takes).</summary>
    public static readonly DependencyProperty TabProperty = DependencyProperty.RegisterAttached(
        "Tab", typeof(object), typeof(CardSearch), new PropertyMetadata(null));

    public static object? GetTab(DependencyObject d) => d.GetValue(TabProperty);
    public static void SetTab(DependencyObject d, object? value) => d.SetValue(TabProperty, value);

    /// <summary>On a section: the tab's name as the tab strip shows it, for a result's breadcrumb.</summary>
    public static readonly DependencyProperty TabLabelProperty = DependencyProperty.RegisterAttached(
        "TabLabel", typeof(string), typeof(CardSearch), new PropertyMetadata(string.Empty));

    public static string GetTabLabel(DependencyObject d) => (string)d.GetValue(TabLabelProperty);
    public static void SetTabLabel(DependencyObject d, string value) => d.SetValue(TabLabelProperty, value);

    /// <summary>On the host: runs with a section's <c>Tab</c> to show that tab. A page already on its
    /// "All" tab leaves it there, since the card is on screen.</summary>
    public static readonly DependencyProperty GoToProperty = DependencyProperty.RegisterAttached(
        "GoTo", typeof(ICommand), typeof(CardSearch), new PropertyMetadata(null));

    public static ICommand? GetGoTo(DependencyObject d) => (ICommand?)d.GetValue(GoToProperty);
    public static void SetGoTo(DependencyObject d, ICommand? value) => d.SetValue(GoToProperty, value);

    private static readonly DependencyProperty ReapplyPendingProperty = DependencyProperty.RegisterAttached(
        "ReapplyPending", typeof(bool), typeof(CardSearch), new PropertyMetadata(false));

    private static void ScheduleReapply(Panel host)
    {
        if ((bool)host.GetValue(ReapplyPendingProperty)) return;
        host.SetValue(ReapplyPendingProperty, true);
        host.Dispatcher.InvokeAsync(() =>
        {
            host.SetValue(ReapplyPendingProperty, false);
            Apply(host, GetQuery(host));
        }, DispatcherPriority.DataBind);
    }

    /// <summary>Marks the query's words in the chosen card or row. Nothing is hidden.</summary>
    internal static void Apply(Panel host, string? query) =>
        SearchHighlight.Refresh(host, SplitTerms(query));

    /// <summary>The most hits a dropdown lists; a longer list is a query that needs another word.</summary>
    internal const int MaxHits = 12;

    /// <summary>
    /// Every card on every tab that matches, best first: a title with all the words, then a title with
    /// some, then the rest in page order. A card its own rule has hidden (a setting for hardware this PC
    /// lacks) isn't offered.
    /// </summary>
    internal static List<CardHit> Find(Panel host, string? query) => Find(host, query, out _);

    /// <param name="total">How many cards matched, of which the first <see cref="MaxHits"/> are returned.</param>
    internal static List<CardHit> Find(Panel host, string? query, out int total)
    {
        string[] terms = SplitTerms(query);
        var scored = new List<(int Rank, CardHit Hit)>();
        total = 0;
        if (terms.Length == 0) return [];

        Gather(host, terms, scored);

        total = scored.Count;
        return scored.OrderBy(s => s.Rank).Select(s => s.Hit).Take(MaxHits).ToList();
    }

    private static void Gather(Panel host, string[] terms, List<(int Rank, CardHit Hit)> scored)
    {
        foreach (Panel section in host.Children.OfType<Panel>())
        {
            object? tab = GetTab(section);
            string tabLabel = GetTabLabel(section);
            foreach (UIElement card in section.Children)
            {
                if (GetIsHeader(card) || card.Visibility != Visibility.Visible) continue;
                Consider(host, card, tab, tabLabel, terms, scored);
            }
        }
    }

    private static void Consider(Panel host, UIElement card, object? tab, string tabLabel, string[] terms, List<(int Rank, CardHit Hit)> scored)
    {
        var pieces = new List<string>();
        Collect(card, pieces.Add);
        if (!Matches(string.Join(' ', pieces) + " " + tabLabel, terms)) return;

        string title = TitleOf(card, pieces);
        if (title.Length == 0) title = tabLabel;
        int rank = Matches(title, terms) ? 0 : terms.Any(t => title.Contains(t, StringComparison.OrdinalIgnoreCase)) ? 1 : 2;
        scored.Add((rank, new CardHit(card, host, tab, tabLabel, title, SnippetOf(pieces, title, terms))));
    }

    /// <summary>A card's name: the first readable label in it, which every card leads with.</summary>
    private static string TitleOf(UIElement card, List<string> pieces)
    {
        // An icon-font glyph is one character; a title is a word. A card that opens with a sentence has no
        // heading of its own, and the sentence is its snippet, so the tab names it instead.
        foreach (string piece in pieces)
        {
            string text = piece.Trim();
            if (text.Length > 2) return text.Length <= MaxTitle ? text : string.Empty;
        }
        return string.Empty;
    }

    private const int MaxTitle = 60;

    /// <summary>The first line, other than the title, that holds one of the words - shortened around the match.</summary>
    private static string SnippetOf(List<string> pieces, string title, string[] terms)
    {
        foreach (string piece in pieces)
        {
            string text = piece.Trim();
            if (text.Length == 0 || text == title) continue;
            int at = terms.Select(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
            if (at < 0) continue;
            const int Max = 110;
            if (text.Length <= Max) return text;
            int start = Math.Max(0, at - 40);
            int length = Math.Min(Max, text.Length - start);
            return (start > 0 ? "\u2026" : "") + text.Substring(start, length).Trim() + (start + length < text.Length ? "\u2026" : "");
        }
        return string.Empty;
    }

    internal static string[] SplitTerms(string? query) =>
        string.IsNullOrWhiteSpace(query) ? [] : query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    internal static bool Matches(string text, IReadOnlyCollection<string> terms) =>
        terms.All(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Everything a person can read in an element: TextBlock text, string content and headers (check
    /// boxes, buttons, radio buttons), and string tooltips. Walks the logical tree, so collapsed rows
    /// count too. What's typed into a TextBox isn't included - an API key shouldn't match a search.
    /// </summary>
    internal static string CollectText(DependencyObject root)
    {
        var text = new StringBuilder();
        Collect(root, piece => text.Append(piece).Append(' '));
        return text.ToString();
    }

    private static void Collect(object node, Action<string> add)
    {
        if (node is string s)
        {
            add(s);
            return;
        }
        if (node is TextBlock block && block.Text.Length > 0) add(block.Text);
        if (node is HeaderedContentControl { Header: string header }) add(header);

        // A tooltip is read after what its element shows, so a card's title and the line that matched are
        // visible text and never a tooltip that happens to sit on the first control.
        string? tip = (node as FrameworkElement)?.ToolTip as string;
        if (node is TextBox)
        {
            if (tip != null) add(tip);
            return;
        }

        // Rows generated from an ItemsSource (About's help topics) aren't logical children, so read
        // what those rows show instead. A list that hasn't generated a row - a dropdown never opened,
        // a list never on screen - still offers its plain text items.
        if (node is ItemsControl { ItemsSource: not null } list)
        {
            foreach (object item in list.Items)
            {
                if (list.ItemContainerGenerator.ContainerFromItem(item) is DependencyObject row)
                    CollectShown(row, add);
                else if (item is string itemText)
                    add(itemText);
            }
            if (tip != null) add(tip);
            return;
        }

        if (node is DependencyObject d)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(d))
                Collect(child, add);
        }
        if (tip != null) add(tip);
    }

    private static void CollectShown(DependencyObject node, Action<string> add)
    {
        if (node is TextBox) return;
        if (node is TextBlock block && block.Text.Length > 0) add(block.Text);
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
            CollectShown(VisualTreeHelper.GetChild(node, i), add);
    }

    /// <summary>
    /// Shows a hit: the host's tab command, then - once the new tab has a layout - the matched row (or
    /// the card, when it has no rows) scrolled to about a sixth of the way down and outlined. The outline
    /// stays while the search does: a new query, or the next result, replaces it.
    /// </summary>
    internal static void GoTo(CardHit hit)
    {
        Panel host = hit.Host;
        if (GetGoTo(host) is { } command && command.CanExecute(hit.Tab)) command.Execute(hit.Tab);

        host.Dispatcher.InvokeAsync(() =>
        {
            host.UpdateLayout();
            if (RowOf(hit.Card, SplitTerms(GetQuery(host))) is not { } target) return;
            if (host.Parent is ScrollViewer viewer && viewer.ViewportHeight > 0 && target.IsVisible)
            {
                double y = target.TranslatePoint(new Point(0, 0), viewer).Y;
                viewer.ScrollToVerticalOffset(Math.Max(0, viewer.VerticalOffset + y - viewer.ViewportHeight * 0.15));
            }
            Outline(host, target);
            host.SetValue(HighlightRootProperty, target);
            Apply(host, GetQuery(host));
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// What to outline for a card: the list row holding the first match (a tweak among six), else the card
    /// itself. A big card outlined whole doesn't say where to look.
    /// </summary>
    internal static FrameworkElement? RowOf(UIElement card, string[] terms)
    {
        if (card is not FrameworkElement fallback) return null;
        foreach (TextBlock block in SearchHighlight.VisibleTextBlocks(card))
        {
            if (SearchHighlight.FindRanges(block.Text, terms).Count == 0) continue;
            for (DependencyObject? up = block; up != null && up != card; up = VisualTreeHelper.GetParent(up))
            {
                if (up is FrameworkElement row && ItemsControl.ItemsControlFromItemContainer(row) != null) return row;
            }
            break;
        }
        return fallback;
    }

    /// <summary>On the host: the card or row whose words are marked. Null (no result chosen) marks nothing.</summary>
    internal static readonly DependencyProperty HighlightRootProperty = DependencyProperty.RegisterAttached(
        "HighlightRoot", typeof(FrameworkElement), typeof(CardSearch), new PropertyMetadata(null));

    internal static FrameworkElement? GetHighlightRoot(DependencyObject d) => (FrameworkElement?)d.GetValue(HighlightRootProperty);

    private static readonly DependencyProperty OutlineProperty = DependencyProperty.RegisterAttached(
        "Outline", typeof(Adorner), typeof(CardSearch), new PropertyMetadata(null));

    private static void ClearOutline(Panel host)
    {
        if (host.GetValue(OutlineProperty) is not Adorner old) return;
        (old as OutlineAdorner)?.Detach();
        (VisualTreeHelper.GetParent(old) as AdornerLayer)?.Remove(old);
        host.ClearValue(OutlineProperty);
    }

    private static void Outline(Panel host, FrameworkElement target)
    {
        ClearOutline(host);
        if (AdornerLayer.GetAdornerLayer(target) is not { } layer) return;
        var adorner = new OutlineAdorner(target);
        layer.Add(adorner);
        host.SetValue(OutlineProperty, adorner);
    }

    private sealed class OutlineAdorner : Adorner
    {
        private const double SideRoom = 8, EdgeRoom = 5;
        private readonly ScrollViewer? _viewer;
        private readonly FrameworkElement? _port;

        public OutlineAdorner(FrameworkElement target) : base(target)
        {
            IsHitTestVisible = false;
            for (DependencyObject? up = VisualTreeHelper.GetParent(target); up != null; up = VisualTreeHelper.GetParent(up))
            {
                if (up is not ScrollViewer viewer) continue;
                _viewer = viewer;
                _port = viewer.Template?.FindName("PART_ScrollContentPresenter", viewer) as FrameworkElement ?? viewer;
                viewer.ScrollChanged += OnScrolled;
                break;
            }
            // A row rebuilt under the search (System's Refresh) takes its outline with it, rather than
            // leaving a box drawn where it was.
            target.Unloaded += OnTargetUnloaded;
        }

        public void Detach()
        {
            if (_viewer != null) _viewer.ScrollChanged -= OnScrolled;
            if (AdornedElement is FrameworkElement target) target.Unloaded -= OnTargetUnloaded;
        }

        private void OnTargetUnloaded(object sender, RoutedEventArgs e)
        {
            Detach();
            (VisualTreeHelper.GetParent(this) as AdornerLayer)?.Remove(this);
        }

        private void OnScrolled(object sender, ScrollChangedEventArgs e) => InvalidateVisual();

        protected override void OnRender(DrawingContext drawingContext)
        {
            // The theme's accent, drawn a little outside the row so it clears the text - but never past
            // the scroll area's edge, where the page would cut it off: a card against the left or top
            // gets the room it has, not the room it would like.
            var pen = new Pen(TrayTrigger.Services.AppPalette.Brush("BrushAccent"), 1.5);
            Size size = AdornedElement.RenderSize;
            double left = SideRoom, right = SideRoom, top = EdgeRoom, bottom = EdgeRoom;
            if (_port != null && AdornedElement.IsDescendantOf(_port))
            {
                Point at = AdornedElement.TranslatePoint(new Point(0, 0), _port);
                left = Math.Clamp(at.X, 0, SideRoom);
                top = Math.Clamp(at.Y, 0, EdgeRoom);
                right = Math.Clamp(_port.ActualWidth - (at.X + size.Width), 0, SideRoom);
                bottom = Math.Clamp(_port.ActualHeight - (at.Y + size.Height), 0, EdgeRoom);
            }
            // Half the pen's width stays inside the clip too.
            drawingContext.DrawRoundedRectangle(null, pen,
                new Rect(-left + 1, -top + 1, size.Width + left + right - 2, size.Height + top + bottom - 2), 8, 8);
        }
    }
}
