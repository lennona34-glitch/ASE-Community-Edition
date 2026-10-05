using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ASE.Services;
using TinyDialogsNet;

namespace ASE;

public class ArtworkCandidateItem : INotifyPropertyChanged
{
    private Bitmap _image;

    public ArtworkCandidate Candidate { get; set; }

    public Bitmap Image
    {
        get => _image;
        set
        {
            if (_image != value)
            {
                _image = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasImage));
            }
        }
    }

    public bool HasImage => _image != null;

    public event PropertyChangedEventHandler PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public partial class ArtworkPickerWindow : Window
{
    private readonly string _gameTitle;
    private readonly string _gameId;
    private readonly string _targetBoxPath;
    private readonly string _currentMediaDir;
    private readonly ArtworkScraperService _scraperService = new ArtworkScraperService();
    private CancellationTokenSource _searchCts;
    private CancellationTokenSource _thumbsCts;

    public ObservableCollection<ArtworkCandidateItem> CandidateItems { get; } = new ObservableCollection<ArtworkCandidateItem>();

    public string ResultCoverPath { get; private set; }
    public bool ResultRemoveCover { get; private set; }

    public ArtworkPickerWindow()
    {
        InitializeComponent();
    }

    public ArtworkPickerWindow(string gameTitle, string gameId, string targetBoxPath, string currentMediaDir) : this()
    {
        _gameTitle = gameTitle;
        _gameId = gameId;
        _targetBoxPath = targetBoxPath;
        _currentMediaDir = currentMediaDir;

        TextGameTitle.Text = string.IsNullOrWhiteSpace(gameTitle) ? gameId : gameTitle;
        TextSubTitle.Text = $"ID: {gameId}  •  Target: {Path.GetFileName(targetBoxPath)}";
        TextSearchQuery.Text = ArtworkScraperService.CleanGameTitle(_gameTitle);

        CandidatesList.ItemsSource = CandidateItems;
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        await PerformSearchAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _searchCts?.Cancel();
        _thumbsCts?.Cancel();
        base.OnClosed(e);
    }

    private async Task PerformSearchAsync()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        _thumbsCts?.Cancel();
        _thumbsCts = new CancellationTokenSource();
        var thumbToken = _thumbsCts.Token;

        LoadingPanel.IsVisible = true;
        TextEmpty.IsVisible = false;
        CandidateItems.Clear();

        string query = TextSearchQuery.Text?.Trim();
        if (string.IsNullOrEmpty(query))
            query = _gameTitle;

        try
        {
            var candidates = await _scraperService.SearchCandidatesAsync(query, _gameId, _currentMediaDir, token);

            if (!token.IsCancellationRequested)
            {
                foreach (var c in candidates)
                {
                    CandidateItems.Add(new ArtworkCandidateItem { Candidate = c });
                }

                TextEmpty.IsVisible = CandidateItems.Count == 0;

                // Load thumbnails in background
                var itemsArray = CandidateItems.ToArray();
                _ = Task.Run(() => LoadThumbnailsAsync(itemsArray, thumbToken));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            TextEmpty.Text = $"Error searching artwork: {ex.Message}";
            TextEmpty.IsVisible = true;
        }
        finally
        {
            LoadingPanel.IsVisible = false;
        }
    }

    private async Task LoadThumbnailsAsync(ArtworkCandidateItem[] items, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AtariSystemEmulator/1.12");

        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) return;

            try
            {
                Bitmap bmp = null;
                if (item.Candidate.IsLocal && File.Exists(item.Candidate.LocalFilePath))
                {
                    using var stream = File.OpenRead(item.Candidate.LocalFilePath);
                    bmp = Bitmap.DecodeToWidth(stream, 240);
                }
                else if (!string.IsNullOrEmpty(item.Candidate.ThumbnailUrl))
                {
                    byte[] bytes = await client.GetByteArrayAsync(item.Candidate.ThumbnailUrl, ct);
                    if (bytes != null && bytes.Length > 0)
                    {
                        using var ms = new MemoryStream(bytes);
                        bmp = Bitmap.DecodeToWidth(ms, 240);
                    }
                }

                if (bmp != null)
                {
                    Dispatcher.UIThread.Post(() => item.Image = bmp);
                }
            }
            catch { }
        }
    }

    private async void OnSearchClick(object sender, RoutedEventArgs e)
    {
        await PerformSearchAsync();
    }

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await PerformSearchAsync();
        }
    }

    private async void OnSelectCandidateClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not ArtworkCandidateItem item)
            return;

        try
        {
            LoadingPanel.IsVisible = true;
            TextLoadingStatus.Text = "Saving artwork...";

            bool success = await _scraperService.DownloadAndSaveArtworkAsync(item.Candidate, _targetBoxPath);

            if (success)
            {
                ResultCoverPath = _targetBoxPath;
                Close(true);
            }
            else
            {
                TinyDialogs.MessageBox("Error", "Could not save the selected artwork.", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
            }
        }
        catch (Exception ex)
        {
            TinyDialogs.MessageBox("Error", $"Failed to save artwork: {ex.Message}", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
        }
        finally
        {
            LoadingPanel.IsVisible = false;
        }
    }

    private async void OnChooseLocalFileClick(object sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider != null)
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Artwork Image File",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Images (*.png, *.jpg, *.jpeg, *.bmp, *.webp, *.gif)")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.gif" }
                    }
                }
            });

            if (files.Count > 0)
            {
                string localPath = files[0].Path.LocalPath;
                try
                {
                    string dir = Path.GetDirectoryName(_targetBoxPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    File.Copy(localPath, _targetBoxPath, true);
                    ResultCoverPath = _targetBoxPath;
                    Close(true);
                }
                catch (Exception ex)
                {
                    TinyDialogs.MessageBox("Error", $"Could not copy artwork file: {ex.Message}", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                }
            }
        }
    }

    private void OnRemoveArtworkClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(_targetBoxPath))
                File.Delete(_targetBoxPath);

            ResultRemoveCover = true;
            ResultCoverPath = null;
            Close(true);
        }
        catch (Exception ex)
        {
            TinyDialogs.MessageBox("Error", $"Could not remove artwork file: {ex.Message}", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
