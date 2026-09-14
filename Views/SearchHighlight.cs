using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace TrayTrigger.Views;

/// <summary>
/// Find-in-page highlighting for <see cref="CardSearch"/>: every visible TextBlock under the host
/// (including the ones check boxes and buttons generate for their labels) gets an adorner that tints
/// the words of the query, and the first match can be scrolled into view - inside a tall card it could
/// otherwise be a screen further down. Adorners draw over the text, so no label, binding or inline
/// is touched, and they live in the ScrollViewer's own adorner layer, so they scroll and clip with it.
/// </summary>
internal static class SearchHighlight
{
    private static readonly DependencyProperty AdornersProperty = DependencyProperty.RegisterAttached(
        "Adorners", typeof(List<HighlightAdorner>), typeof(SearchHighlight), new PropertyMetadata(null));

    // Bumped on every refresh, so a deferred pass for an older query quietly does nothing.
    private static readonly DependencyProperty PassProperty = DependencyProperty.RegisterAttached(
        "Pass", typeof(int), typeof(SearchHighlight), new PropertyMetadata(0));

    private static readonly DependencyProperty WaitingForVisibleProperty = DependencyProperty.RegisterAttached(
        "WaitingForVisible", typeof(bool), typeof(SearchHighlight), new PropertyMetadata(false));

    // A pass that should park the first match hasn't run yet. A refresh that doesn't move (the page
    // re-running the same search right after the query changed) supersedes that pass, so it inherits
    // the move instead of cancelling it.
    private static readonly DependencyProperty MovePendingProperty = DependencyProperty.RegisterAttached(
        "MovePending", typeof(bool), typeof(SearchHighlight), new PropertyMetadata(false));

    /// <param name="moveToFirstMatch">Park the first match in view - when the query changed, not when
    /// the page changed under an existing search.</param>
    internal static void Refresh(Panel host, string[] terms, bool moveToFirstMatch)
    {
        Clear(host);
        int pass = (int)host.GetValue(PassProperty) + 1;
        host.SetValue(PassProperty, pass);
        if (moveToFirstMatch) host.SetValue(MovePendingProperty, true);

        // One-letter words would tint nearly every label on the page - the slowest pass there is, for
        // no benefit. They still filter cards; they just aren't highlighted.
        string[] highlightTerms = terms.Where(term => term.Length >= 2).ToArray();
        if (highlightTerms.Length == 0)
        {
            host.ClearValue(MovePendingProperty);
            return;
        }

        // A search typed before leaving the page is still there on the way back; highlight it then.
        if (!host.IsVisible)
        {
            WaitForVisible(host);
            return;
        }

        // Cards that were collapsed a moment ago have no template or layout yet; Loaded priority
        // runs after the layout pass that gives them one.
        host.Dispatcher.InvokeAsync(() =>
        {
            if ((int)host.GetValue(PassProperty) != pass) return;
            // Left the page in the moment before this ran: nothing's visible to highlight yet.
            if (!host.IsVisible)
            {
                WaitForVisible(host);
                return;
            }

            var adorners = new List<HighlightAdorner>();
            TextBlock? firstBlock = null;
            Rect firstRect = Rect.Empty;
            foreach (TextBlock block in VisibleTextBlocks(host))
            {
                // Plain string check first: asking a TextBlock for text positions switches it to its
                // slower rich-text form for good, so only blocks that actually match pay for that.
                if (FindRanges(block.Text, highlightTerms).Count == 0) continue;
                List<Rect> rects = MatchRects(block, highlightTerms);
                if (rects.Count == 0) continue;
                if (AdornerLayer.GetAdornerLayer(block) is not { } layer) continue;

                var adorner = new HighlightAdorner(block, highlightTerms, rects);
                layer.Add(adorner);
                adorners.Add(adorner);
                if (firstBlock == null)
                {
                    firstBlock = block;
                    firstRect = rects[0];
                }
            }
            host.SetValue(AdornersProperty, adorners);

            // Park the first match about a third of the way down unless it's already in the upper part
            // of the view: just scrolled into view, it sits on the bottom edge and still reads as
            // "somewhere below".
            bool move = (bool)host.GetValue(MovePendingProperty);
            host.ClearValue(MovePendingProperty);
            if (move && firstBlock != null && host.Parent is ScrollViewer viewer && viewer.ViewportHeight > 0)
            {
                double y = firstBlock.TranslatePoint(firstRect.TopLeft, viewer).Y;
                if (y < 0 || y > viewer.ViewportHeight * 0.6)
                    viewer.ScrollToVerticalOffset(viewer.VerticalOffset + y - viewer.ViewportHeight / 3);
            }
        }, DispatcherPriority.Loaded);
    }

    private static void WaitForVisible(Panel host)
    {
        // Coming back to the page highlights where the reader left off, so nothing is left to move.
        host.ClearValue(MovePendingProperty);
        if ((bool)host.GetValue(WaitingForVisibleProperty)) return;
        host.SetValue(WaitingForVisibleProperty, true);
        host.IsVisibleChanged += OnHostBecameVisible;
    }

    private static void OnHostBecameVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Panel host || !host.IsVisible) return;
        host.IsVisibleChanged -= OnHostBecameVisible;
        host.SetValue(WaitingForVisibleProperty, false);
        // Back on the page: highlight where the reader left off rather than jumping to the first match.
        Refresh(host, CardSearch.SplitTerms(CardSearch.GetQuery(host)), moveToFirstMatch: false);
    }

    private static void Clear(Panel host)
    {
        if (host.GetValue(AdornersProperty) is not List<HighlightAdorner> adorners) return;
        foreach (var adorner in adorners)
        {
            adorner.Detach();
            (VisualTreeHelper.GetParent(adorner) as AdornerLayer)?.Remove(adorner);
        }
        host.ClearValue(AdornersProperty);
    }

    /// <summary>Visible TextBlocks in on-screen order, skipping anything collapsed and the insides of
    /// text boxes (their placeholder text isn't something the search found).</summary>
    private static IEnumerable<TextBlock> VisibleTextBlocks(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { IsVisible: false } or TextBox) continue;
            if (child is TextBlock block) yield return block;
            foreach (TextBlock nested in VisibleTextBlocks(child)) yield return nested;
        }
    }

    /// <summary>Where the query's words sit in a laid-out TextBlock: one rectangle per line a match
    /// covers, in the TextBlock's own coordinates.</summary>
    internal static List<Rect> MatchRects(TextBlock block, IReadOnlyCollection<string> terms)
    {
        var rects = new List<Rect>();
        for (TextPointer? run = block.ContentStart;
             run != null && run.CompareTo(block.ContentEnd) < 0;
             run = run.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (run.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text) continue;
            foreach (var (start, length) in FindRanges(run.GetTextInRun(LogicalDirection.Forward), terms))
                AddRangeRects(run, start, length, rects);
        }
        return rects;
    }

    // Usually one rectangle from the match's two ends; only a match that wraps onto the next line is
    // walked character by character, so it gets a rectangle on each line.
    private static void AddRangeRects(TextPointer runStart, int start, int length, List<Rect> rects)
    {
        TextPointer? first = runStart.GetPositionAtOffset(start, LogicalDirection.Forward);
        TextPointer? last = runStart.GetPositionAtOffset(start + length, LogicalDirection.Backward);
        if (first == null || last == null) return;
        Rect startEdge = first.GetCharacterRect(LogicalDirection.Forward);
        Rect endEdge = last.GetCharacterRect(LogicalDirection.Backward);
        if (startEdge.IsEmpty || endEdge.IsEmpty) return;
        if (Math.Abs(startEdge.Top - endEdge.Top) <= 0.5)
        {
            rects.Add(new Rect(new Point(startEdge.Left, startEdge.Top), new Point(endEdge.Left, startEdge.Bottom)));
            return;
        }

        Rect line = Rect.Empty;
        for (int i = start; i < start + length; i++)
        {
            TextPointer? before = runStart.GetPositionAtOffset(i, LogicalDirection.Forward);
            TextPointer? after = runStart.GetPositionAtOffset(i + 1, LogicalDirection.Backward);
            if (before == null || after == null) break;

            Rect left = before.GetCharacterRect(LogicalDirection.Forward);
            Rect right = after.GetCharacterRect(LogicalDirection.Backward);
            if (left.IsEmpty || right.IsEmpty) continue;

            var character = new Rect(new Point(left.Left, left.Top), new Point(right.Left, left.Bottom));
            if (!line.IsEmpty && Math.Abs(line.Top - character.Top) > 0.5)
            {
                rects.Add(line);
                line = Rect.Empty;
            }
            line = line.IsEmpty ? character : Rect.Union(line, character);
        }
        if (!line.IsEmpty) rects.Add(line);
    }

    /// <summary>Every occurrence of every term in <paramref name="text"/>, case-insensitive, with
    /// overlapping and touching hits merged ("tray" and "ray" in "tray" is one highlight).</summary>
    internal static List<(int Start, int Length)> FindRanges(string text, IReadOnlyCollection<string> terms)
    {
        var hits = new List<(int Start, int End)>();
        foreach (string term in terms)
        {
            if (term.Length == 0) continue;
            for (int i = text.IndexOf(term, StringComparison.OrdinalIgnoreCase); i >= 0;
                 i = text.IndexOf(term, i + term.Length, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add((i, i + term.Length));
            }
        }
        hits.Sort();

        var merged = new List<(int Start, int Length)>();
        int spanStart = -1, spanEnd = -1;
        foreach (var (hitStart, hitEnd) in hits)
        {
            if (spanStart >= 0 && hitStart <= spanEnd)
            {
                spanEnd = Math.Max(spanEnd, hitEnd);
                continue;
            }
            if (spanStart >= 0) merged.Add((spanStart, spanEnd - spanStart));
            (spanStart, spanEnd) = (hitStart, hitEnd);
        }
        if (spanStart >= 0) merged.Add((spanStart, spanEnd - spanStart));
        return merged;
    }

    internal sealed class HighlightAdorner : Adorner
    {
        // Find-in-page amber: translucent, so the label stays readable through it.
        private static readonly Brush Fill = CreateFill();

        private readonly string[] _terms;
        private readonly TextBlock _block;
        // Where the words were, and the text and size that was measured against. Live text (System's
        // telemetry, every 3 s) can move a word without the label changing size, so both are compared.
        private List<Rect> _rects;
        private string _rectsText;
        private Size _rectsSize;

        public HighlightAdorner(TextBlock block, string[] terms, List<Rect> rects) : base(block)
        {
            _block = block;
            _terms = terms;
            _rects = rects;
            _rectsText = block.Text;
            _rectsSize = block.RenderSize;
            IsHitTestVisible = false;
            // A row collapsed later (its parent option switched off) takes its highlight with it.
            _block.IsVisibleChanged += OnBlockVisibilityChanged;
            _block.LayoutUpdated += OnBlockLayoutUpdated;
        }

        public void Detach()
        {
            _block.IsVisibleChanged -= OnBlockVisibilityChanged;
            _block.LayoutUpdated -= OnBlockLayoutUpdated;
        }

        private void OnBlockVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => InvalidateVisual();

        private void OnBlockLayoutUpdated(object? sender, EventArgs e)
        {
            if (!_block.IsVisible) return;
            string text = _block.Text;
            if (text == _rectsText && _block.RenderSize == _rectsSize) return;
            _rects = MatchRects(_block, _terms);
            _rectsText = text;
            _rectsSize = _block.RenderSize;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (!_block.IsVisible) return;
            foreach (Rect rect in _rects)
                drawingContext.DrawRoundedRectangle(Fill, null, rect, 2, 2);
        }

        private static Brush CreateFill()
        {
            var brush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xC4, 0x00));
            brush.Freeze();
            return brush;
        }
    }
}
