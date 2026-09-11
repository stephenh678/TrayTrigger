using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TrayTrigger.Services;

namespace TrayTrigger.Views;

/// <summary>One search result in the match picker, source-agnostic: a stable id (Steam App ID or
/// RAWG id as a string), the title, and a one-line subtitle (year/platforms, or the App ID).</summary>
public sealed record MatchPickerHit(string Id, string Name, string Subtitle);

/// <summary>Outcome of one picker search: the hits, plus a message to show when there are none
/// (bad key, offline, nothing found).</summary>
public sealed record MatchPickerResult(List<MatchPickerHit> Hits, string? EmptyMessage);

/// <summary>Row wrapper: a hit plus whether it is the game's current match.</summary>
public sealed class MatchPickerItem
{
    public MatchPickerHit Hit { get; }
    public bool IsCurrent { get; }
    public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;

    public MatchPickerItem(MatchPickerHit hit, bool isCurrent)
    {
        Hit = hit;
        IsCurrent = isCurrent;
    }
}

/// <summary>
/// "Change match" - a live title search the user drives by hand when the automatic name match
/// picked the wrong game or found nothing. Source-agnostic: the caller supplies the search
/// delegate (Steam store search or RAWG), the current id to flag, and an optional attribution
/// link (RAWG's terms require one). Pre-fills and runs the game's name; the pick is returned in
/// <see cref="SelectedHit"/>. Modelled on <see cref="GameCandidatePickerDialog"/>.
/// </summary>
public partial class GameMatchPickerDialog : Window
{
    private readonly Func<string, CancellationToken, Task<MatchPickerResult>> _search;
    private readonly string? _currentId;
    private readonly string? _attributionUrl;
    private CancellationTokenSource? _searchCts;

    public MatchPickerHit? SelectedHit { get; private set; }

    /// <param name="title">Window/heading text, e.g. "Change Steam Match".</param>
    /// <param name="subtitle">One line under the heading.</param>
    /// <param name="initialQuery">Pre-filled and searched on open (the game's name).</param>
    /// <param name="currentId">The game's current match id, flagged CURRENT and pre-selected when it appears.</param>
    /// <param name="search">Runs a search for a query; never throws (return an empty result with a message instead).</param>
    /// <param name="attributionLabel">Optional link text shown under the list (e.g. "Data from RAWG").</param>
    /// <param name="attributionUrl">Target of that link.</param>
    public GameMatchPickerDialog(
        string title,
        string subtitle,
        string initialQuery,
        string? currentId,
        Func<string, CancellationToken, Task<MatchPickerResult>> search,
        string? attributionLabel = null,
        string? attributionUrl = null)
    {
        InitializeComponent();
        WindowThemeService.PrepareForFirstShow(this);

        _search = search;
        _currentId = currentId;
        _attributionUrl = attributionUrl;

        Owner = WindowHelper.ActiveOwner();

        Title = title;
        TitleTextBlock.Text = title;
        SubtitleTextBlock.Text = subtitle;
        SearchTextBox.Text = initialQuery;

        if (!string.IsNullOrWhiteSpace(attributionLabel) && !string.IsNullOrWhiteSpace(attributionUrl))
        {
            AttributionRun.Text = attributionLabel;
            AttributionTextBlock.Visibility = Visibility.Visible;
        }

        Loaded += async (s, e) =>
        {
            WindowThemeService.CenterOverOwner(this);
            Activate();
            SearchTextBox.Focus();
            SearchTextBox.SelectAll();
            await RunSearchAsync();
        };

        Closed += (s, e) => _searchCts?.Cancel();
    }

    private async Task RunSearchAsync()
    {
        string query = SearchTextBox.Text.Trim();
        if (query.Length == 0)
            return;

        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        SearchButton.IsEnabled = false;
        UseButton.IsEnabled = false;
        ResultsListBox.ItemsSource = null;
        ShowEmpty("Searching…");

        try
        {
            MatchPickerResult result;
            try
            {
                result = await _search(query, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                LoggingService.Warn("GameMatchPickerDialog", $"Search for '{query}' failed: {ex.Message}");
                result = new MatchPickerResult(new List<MatchPickerHit>(), "Search failed. Check your internet connection and try again.");
            }

            if (cts.IsCancellationRequested)
                return;

            var items = result.Hits
                .Select(h => new MatchPickerItem(h, !string.IsNullOrEmpty(_currentId) && h.Id == _currentId))
                .ToList();
            ResultsListBox.ItemsSource = items;

            if (items.Count > 0)
            {
                ShowEmpty(null);
                ResultsListBox.SelectedIndex = Math.Max(0, items.FindIndex(i => i.IsCurrent));
            }
            else
            {
                ShowEmpty(result.EmptyMessage ?? $"No results for “{query}”. Try a shorter or more official title.");
            }
        }
        finally
        {
            if (!cts.IsCancellationRequested)
                SearchButton.IsEnabled = true;
        }
    }

    private void ShowEmpty(string? message)
    {
        EmptyTextBlock.Text = message ?? string.Empty;
        EmptyTextBlock.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e) => await RunSearchAsync();

    private async void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await RunSearchAsync();
        }
    }

    private void ResultsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UseButton.IsEnabled = ResultsListBox.SelectedItem is MatchPickerItem;
    }

    private void ResultsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ConfirmSelection();

    private void UseButton_Click(object sender, RoutedEventArgs e) => ConfirmSelection();

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void AttributionLink_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_attributionUrl))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(_attributionUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LoggingService.Warn("GameMatchPickerDialog", $"Failed to open '{_attributionUrl}': {ex.Message}");
        }
    }

    private void ConfirmSelection()
    {
        if (ResultsListBox.SelectedItem is MatchPickerItem item)
        {
            SelectedHit = item.Hit;
            DialogResult = true;
            Close();
        }
    }
}
