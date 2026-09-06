using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TrayTrigger.Services;

namespace TrayTrigger.Views;

public class CandidatePickerItem : INotifyPropertyChanged
{
    private System.Windows.Media.ImageSource? _iconImage;

    public GameCandidate Candidate { get; }
    public System.Windows.Media.ImageSource? IconImage
    {
        get => _iconImage;
        private set
        {
            if (_iconImage != value)
            {
                _iconImage = value;
                OnPropertyChanged();
            }
        }
    }
    public string DisplaySize => Candidate.DisplaySize;
    public string DisplayPath => Candidate.DisplayPath;
    public bool IsRecommended { get; }
    public Visibility IsRecommendedVisibility => IsRecommended ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public CandidatePickerItem(GameCandidate candidate, bool isRecommended = false)
    {
        Candidate = candidate;
        IsRecommended = isRecommended;

        _ = Task.Run(() =>
        {
            try
            {
                var img = ExtractExeIcon(candidate.ExePath);
                if (img != null)
                {
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        IconImage = img;
                    });
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("GameCandidatePickerDialog", $"Failed to extract icon for '{candidate.ExePath}': {ex.Message}");
            }
        });
    }

    private static System.Windows.Media.ImageSource? ExtractExeIcon(string path)
    {
        return IconExtractorService.ExtractAssociatedBitmapSource(path);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public partial class GameCandidatePickerDialog : Window
{
    public GameCandidate? SelectedCandidate { get; private set; }

    public GameCandidatePickerDialog(string folderPath, List<GameCandidate> candidates)
    {
        InitializeComponent();

        if (Application.Current?.MainWindow is { IsVisible: true } main)
        {
            Owner = main;
        }

        Loaded += (s, e) =>
        {
            if (Owner != null)
            {
                Left = Owner.Left + (Owner.ActualWidth - ActualWidth) / 2;
                Top = Owner.Top + (Owner.ActualHeight - ActualHeight) / 2;
            }
            Activate();
        };

        string folderName = Path.GetFileName(folderPath.TrimEnd('\\', '/'));
        SubtitleTextBlock.Text = $"Found {candidates.Count} executable(s) across the folder branch. Select the game executable:";

        var items = new List<CandidatePickerItem>();
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            bool isRec = (i == 0 && c.ConfidenceScore >= 50);
            items.Add(new CandidatePickerItem(c, isRec));
        }

        CandidatesListBox.ItemsSource = items;
        if (items.Count > 0)
        {
            CandidatesListBox.SelectedIndex = 0;
        }
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        ConfirmSelection();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void CandidatesListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ConfirmSelection();
    }

    private void ConfirmSelection()
    {
        if (CandidatesListBox.SelectedItem is CandidatePickerItem item)
        {
            SelectedCandidate = item.Candidate;
            DialogResult = true;
            Close();
        }
    }
}
