using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

/// <summary>
/// Which collections a game (or the selected games) is in, or which ones a tool is limited to. Laid out like
/// Change Category: a box to pick or type, Add beside it, and what is in shows below as a chip per collection.
/// With <paramref name="validateNew"/> a new collection's name can be typed; without it only the existing
/// ones can be picked.
/// </summary>
public partial class CollectionsDialog : Window
{
    private readonly ObservableCollection<CollectionChoice> _choices;
    private readonly ObservableCollection<CollectionChoice> _chosen = new();
    private readonly ObservableCollection<string> _available = new();
    private readonly Func<string, string?>? _validateNew;
    private readonly string _emptyText;
    private readonly string _noneChosenText;

    public CollectionsDialog(string heading, string prompt, IEnumerable<CollectionChoice> choices,
        Func<string, string?>? validateNew, string emptyText = "", string noneChosenText = "")
    {
        InitializeComponent();
        WindowThemeService.PrepareForFirstShow(this);

        Title = heading;
        HeadingTextBlock.Text = heading;
        PromptTextBlock.Text = prompt;
        Owner = WindowHelper.ActiveOwner();

        _choices = new ObservableCollection<CollectionChoice>(choices);
        _validateNew = validateNew;
        _emptyText = emptyText;
        _noneChosenText = noneChosenText;
        ChosenList.ItemsSource = _chosen;
        NameComboBox.ItemsSource = _available;
        // Picking only (the tool's limit): the names come from the list, nothing new can be typed.
        if (validateNew == null) NameComboBox.IsEditable = false;
        Rebuild();

        Loaded += (_, _) =>
        {
            WindowThemeService.CenterOverOwner(this);
            Activate();
            NameComboBox.Focus();
        };
    }

    /// <summary>Collections to put the games in: in the list now, and not before.</summary>
    public IReadOnlyList<string> ToAdd => _choices.Where(c => c.IsChecked == true && c.Initial != true).Select(c => c.Name).ToList();

    /// <summary>Collections to take the games out of: removed from the list, and in (or in for some) before.</summary>
    public IReadOnlyList<string> ToRemove => _choices.Where(c => c.IsChecked == false && c.Initial != false).Select(c => c.Name).ToList();

    /// <summary>Every collection left in the list (the tool picker's answer).</summary>
    public IReadOnlyList<string> Ticked => _choices.Where(c => c.IsChecked == true).Select(c => c.Name).ToList();

    /// <summary>The chips show what is in (or, for a selection, in for some); the box offers the rest.</summary>
    private void Rebuild()
    {
        _chosen.Clear();
        _available.Clear();
        foreach (var c in _choices)
        {
            if (c.IsChecked == false) _available.Add(c.Name);
            else _chosen.Add(c);
        }
        // No collections exist at all, or some do and none is chosen: two different things to say.
        string hint = _choices.Count == 0 ? _emptyText : _chosen.Count == 0 ? _noneChosenText : string.Empty;
        EmptyTextBlock.Text = hint;
        EmptyTextBlock.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddButton_Click(object sender, RoutedEventArgs e) => AddNewName();

    private void NameComboBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !NameComboBox.IsEditable) return;
        // Enter adds the typed name, rather than saving the dialog under it.
        e.Handled = true;
        AddNewName();
    }

    private void RemoveChip_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not CollectionChoice choice) return;
        choice.IsChecked = false;
        Rebuild();
    }

    private void AddNewName()
    {
        string name = (NameComboBox.IsEditable ? NameComboBox.Text : NameComboBox.SelectedItem as string)?.Trim() ?? string.Empty;
        if (name.Length == 0) return;

        var same = _choices.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (same != null)
        {
            same.IsChecked = true;
        }
        else
        {
            string? problem = _validateNew?.Invoke(name);
            if (problem != null)
            {
                ErrorTextBlock.Text = problem;
                ErrorTextBlock.Visibility = Visibility.Visible;
                return;
            }
            _choices.Add(new CollectionChoice(name, false) { IsChecked = true });
        }
        ErrorTextBlock.Visibility = Visibility.Collapsed;
        Rebuild();
        NameComboBox.SelectedItem = null;
        NameComboBox.Text = string.Empty;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // A name typed, or picked from the box (the tool's limit, where nothing can be typed), but not added is still
        // meant: add it before saving.
        string pending = (NameComboBox.IsEditable ? NameComboBox.Text : NameComboBox.SelectedItem as string)?.Trim() ?? string.Empty;
        if (pending.Length > 0)
        {
            AddNewName();
            if (ErrorTextBlock.Visibility == Visibility.Visible) return;
        }
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
