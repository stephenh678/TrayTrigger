using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace TrayTrigger.Views;

/// <summary>
/// Card-level search for a sectioned page (Settings, About, System). <c>Query</c> goes on the panel
/// inside the page's ScrollViewer: each Panel child of that host is a section, the section child
/// marked <c>IsHeader</c> is its heading, and every other section child is a card. A card stays
/// visible when every word of the query appears somewhere in its text - titles, descriptions, option
/// labels, tooltips - and a heading that matches keeps its whole section. A section left with no cards
/// hides, heading and all, and <c>NoMatches</c> on the host says the whole page came up empty. The
/// words themselves are highlighted in what's left on screen, first match scrolled into view
/// (<see cref="SearchHighlight"/>). Only the sections the page is showing take part, so on a single
/// tab the search stays within that tab; <c>Scope</c> re-runs it when what's on screen changes.
/// <para>
/// Nothing's own visibility rules are overwritten: an element with no local Visibility is hidden with
/// a local value and restored with ClearValue, so style triggers (the headings' "All tab only") take
/// over again; a bound one (the sections' Show*Section) is hidden with SetCurrentValue and restored by
/// re-reading its binding.
/// </para>
/// </summary>
public static class CardSearch
{
    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query", typeof(string), typeof(CardSearch),
        new PropertyMetadata(null, (d, e) => { if (d is Panel host) Apply(host, e.NewValue as string); }));

    public static string? GetQuery(DependencyObject d) => (string?)d.GetValue(QueryProperty);
    public static void SetQuery(DependencyObject d, string? value) => d.SetValue(QueryProperty, value);

    /// <summary>
    /// What's on screen: the selected tab, plus anything else that adds, removes or rewrites cards
    /// (System binds its specs loading and its tweak state too). A change re-runs the search against
    /// what's there now, leaving the scroll position alone - deferred and coalesced, because this
    /// binding can update before the page's own visibility bindings do.
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

    private static readonly DependencyPropertyKey NoMatchesKey = DependencyProperty.RegisterAttachedReadOnly(
        "NoMatches", typeof(bool), typeof(CardSearch), new PropertyMetadata(false));

    public static readonly DependencyProperty NoMatchesProperty = NoMatchesKey.DependencyProperty;

    public static bool GetNoMatches(DependencyObject d) => (bool)d.GetValue(NoMatchesProperty);

    // Marks what this class collapsed, so a restore only ever undoes its own work.
    private static readonly DependencyProperty HiddenBySearchProperty = DependencyProperty.RegisterAttached(
        "HiddenBySearch", typeof(bool), typeof(CardSearch), new PropertyMetadata(false));

    private static readonly DependencyProperty ReapplyPendingProperty = DependencyProperty.RegisterAttached(
        "ReapplyPending", typeof(bool), typeof(CardSearch), new PropertyMetadata(false));

    private static void ScheduleReapply(Panel host)
    {
        if ((bool)host.GetValue(ReapplyPendingProperty)) return;
        host.SetValue(ReapplyPendingProperty, true);
        host.Dispatcher.InvokeAsync(() =>
        {
            host.SetValue(ReapplyPendingProperty, false);
            Apply(host, GetQuery(host), moveToResults: false);
        }, DispatcherPriority.DataBind);
    }

    /// <param name="moveToResults">True when the query itself changed: back to the top, and the first
    /// match parked in view. A re-run because the page changed under the search leaves the view alone.</param>
    internal static void Apply(Panel host, string? query, bool moveToResults = true)
    {
        string[] terms = SplitTerms(query);
        bool searching = terms.Length > 0;
        bool anyCardShown = false;

        foreach (var section in host.Children.OfType<Panel>())
        {
            // Sections the selected tab hides take no part; undo anything an earlier search hid inside
            // them, so nothing is missing when that tab is picked.
            if (!IsOwnRuleVisible(section))
            {
                foreach (UIElement child in section.Children) SetHidden(child, false);
                continue;
            }

            UIElement? header = section.Children.OfType<UIElement>().FirstOrDefault(GetIsHeader);
            bool headerMatches = searching && header != null && Matches(CollectText(header), terms);
            bool sectionHasCards = false;

            foreach (UIElement child in section.Children)
            {
                if (child == header) continue;
                // A card its own rule hides (System's loading placeholder once specs are in) takes no part.
                if (!IsOwnRuleVisible(child)) continue;
                bool show = !searching || headerMatches || Matches(CollectText(child), terms);
                SetHidden(child, !show);
                // Handing a card back to its own rule is the only way to learn that rule has hidden it
                // since an earlier search did.
                if (show && child.Visibility != Visibility.Visible) continue;
                sectionHasCards |= show;
            }

            bool hideSection = searching && !sectionHasCards;
            if (header != null) SetHidden(header, hideSection);
            SetHidden(section, hideSection);
            // Likewise a section whose tab rule hid it while an earlier search was hiding it.
            if (!hideSection && section.Visibility != Visibility.Visible) continue;
            anyCardShown |= sectionHasCards;
        }

        host.SetValue(NoMatchesKey, searching && !anyCardShown);
        if (moveToResults) (host.Parent as ScrollViewer)?.ScrollToTop();
        SearchHighlight.Refresh(host, terms, moveToResults);
    }

    /// <summary>
    /// Whether the element's own rule (binding, style or default) shows it, read without touching it -
    /// probing by un-hiding would ripple IsVisible through the whole card on every keystroke. One the
    /// search is hiding was showing when it was hidden; if a binding has since pushed Visible over the
    /// search's value, the search's hold is gone, so its marker is dropped. A binding that has since
    /// pushed Collapsed looks the same as the search's own Collapsed; <see cref="Apply"/> finds that
    /// out when it hands the element back.
    /// </summary>
    private static bool IsOwnRuleVisible(UIElement element)
    {
        if (!(bool)element.GetValue(HiddenBySearchProperty)) return element.Visibility == Visibility.Visible;
        if (element.Visibility == Visibility.Visible) element.SetValue(HiddenBySearchProperty, false);
        return true;
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
        Collect(root, text);
        return text.ToString();
    }

    private static void Collect(object node, StringBuilder text)
    {
        if (node is string s)
        {
            text.Append(s).Append(' ');
            return;
        }
        if (node is TextBlock block) text.Append(block.Text).Append(' ');
        if (node is HeaderedContentControl { Header: string header }) text.Append(header).Append(' ');
        if (node is FrameworkElement { ToolTip: string tip }) text.Append(tip).Append(' ');
        if (node is TextBox) return;

        // Rows generated from an ItemsSource (About's help topics) aren't logical children, so read
        // what those rows show instead. A list that hasn't generated a row - a dropdown never opened,
        // a list never on screen - still offers its plain text items.
        if (node is ItemsControl { ItemsSource: not null } list)
        {
            foreach (object item in list.Items)
            {
                if (list.ItemContainerGenerator.ContainerFromItem(item) is DependencyObject row)
                    CollectShown(row, text);
                else if (item is string itemText)
                    text.Append(itemText).Append(' ');
            }
            return;
        }

        if (node is DependencyObject d)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(d))
                Collect(child, text);
        }
    }

    private static void CollectShown(DependencyObject node, StringBuilder text)
    {
        if (node is TextBox) return;
        if (node is TextBlock block) text.Append(block.Text).Append(' ');
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
            CollectShown(VisualTreeHelper.GetChild(node, i), text);
    }

    private static void SetHidden(UIElement element, bool hide)
    {
        if ((bool)element.GetValue(HiddenBySearchProperty) == hide) return;
        element.SetValue(HiddenBySearchProperty, hide);

        var binding = BindingOperations.GetBindingExpressionBase(element, UIElement.VisibilityProperty);
        if (hide)
        {
            if (binding != null) element.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            else element.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        }
        else
        {
            if (binding != null) binding.UpdateTarget();
            else element.ClearValue(UIElement.VisibilityProperty);
        }
    }
}
