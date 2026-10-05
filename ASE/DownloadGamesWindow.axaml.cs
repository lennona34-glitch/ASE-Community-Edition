using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ASE.Models;
using ASE.Services;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TinyDialogsNet;

namespace ASE;

public partial class DownloadGamesWindow : Window
{
    private readonly GameDownloadService _downloadService = new();
    private readonly GameCatalogService _catalogService = new();
    private List<DownloadableGame> _curatedGames = new();
    private CancellationTokenSource _searchCts;

    public ObservableCollection<DownloadableGame> DisplayedGames { get; } = new();

    public string SelectedDiskPath { get; private set; }

    public DownloadGamesWindow()
    {
        InitializeComponent();
        GamesList.ItemsSource = DisplayedGames;
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ASEMain.EnterUiPause();

        string libPath = Config.ConfigOptions.RunninConfig.LibraryPath;
        TextLibraryPath.Text = string.IsNullOrWhiteSpace(libPath) ? "Not configured" : libPath;

        if (!string.IsNullOrEmpty(Config.ConfigOptions.RunninConfig.LastDownloaderSearch))
            TextSearch.Text = Config.ConfigOptions.RunninConfig.LastDownloaderSearch;

        await LoadCuratedGamesAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _searchCts?.Cancel();
        base.OnClosed(e);
        ASEMain.ExitUiPause();
    }

    private async Task LoadCuratedGamesAsync()
    {
        LoadingPanel.IsVisible = true;
        EmptyText.IsVisible = false;
        DisplayedGames.Clear();

        try
        {
            string libPath = Config.ConfigOptions.RunninConfig.LibraryPath;
            _curatedGames = await _catalogService.GetCuratedGamesAsync(libPath);
            ApplyCuratedFilter();
        }
        catch (Exception ex)
        {
            EmptyText.Text = $"Failed to load catalog: {ex.Message}";
            EmptyText.IsVisible = true;
        }
        finally
        {
            LoadingPanel.IsVisible = false;
        }
    }

    private void ApplyCuratedFilter()
    {
        string query = TextSearch.Text?.Trim() ?? "";

        Config.ConfigOptions.RunninConfig.LastDownloaderSearch = TextSearch.Text ?? "";
        Program.Config.DumpJsonConfig();

        DisplayedGames.Clear();

        var matches = string.IsNullOrEmpty(query)
            ? _curatedGames
            : _curatedGames.Where(g =>
                g.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                g.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                g.Category.Contains(query, StringComparison.OrdinalIgnoreCase));

        foreach (var game in matches)
            DisplayedGames.Add(game);

        EmptyText.Text = "No curated games match your search.";
        EmptyText.IsVisible = DisplayedGames.Count == 0;
    }

    private async void OnSourceTabChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        if (TabDirect.IsChecked == true)
        {
            SearchPanel.IsVisible = false;
            DirectLinkPanel.IsVisible = true;
            DisplayedGames.Clear();
            EmptyText.IsVisible = false;
        }
        else if (TabTosec.IsChecked == true)
        {
            SearchPanel.IsVisible = true;
            DirectLinkPanel.IsVisible = false;
            TextSearch.PlaceholderText = "Search TOSEC archive (e.g. Xenon, Double Dragon, Carrier Command)...";
            await PerformTosecSearchAsync();
        }
        else if (TabArchive.IsChecked == true)
        {
            SearchPanel.IsVisible = true;
            DirectLinkPanel.IsVisible = false;
            TextSearch.PlaceholderText = "Search Archive.org retro library (e.g. Pacman, Dungeon, Demo)...";
            await PerformArchiveSearchAsync();
        }
        else // Curated
        {
            SearchPanel.IsVisible = true;
            DirectLinkPanel.IsVisible = false;
            TextSearch.PlaceholderText = "Search curated games (e.g. Oxyd, Lemmings, Pinball)...";
            ApplyCuratedFilter();
        }
    }

    private async void OnSearchClick(object sender, RoutedEventArgs e)
    {
        if (TabTosec.IsChecked == true)
        {
            await PerformTosecSearchAsync();
        }
        else if (TabArchive.IsChecked == true)
        {
            await PerformArchiveSearchAsync();
        }
        else
        {
            ApplyCuratedFilter();
        }
    }

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (TabTosec.IsChecked == true)
            {
                await PerformTosecSearchAsync();
            }
            else if (TabArchive.IsChecked == true)
            {
                await PerformArchiveSearchAsync();
            }
            else
            {
                ApplyCuratedFilter();
            }
        }
    }

    private async Task PerformTosecSearchAsync()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        LoadingPanel.IsVisible = true;
        EmptyText.IsVisible = false;
        DisplayedGames.Clear();

        try
        {
            string query = TextSearch.Text?.Trim() ?? "";
            Config.ConfigOptions.RunninConfig.LastDownloaderSearch = TextSearch.Text ?? "";
            Program.Config.DumpJsonConfig();
            string libPath = Config.ConfigOptions.RunninConfig.LibraryPath;

            var results = await _catalogService.SearchTosecArchiveAsync(query, libPath, token);

            if (!token.IsCancellationRequested)
            {
                foreach (var game in results)
                    DisplayedGames.Add(game);

                EmptyText.Text = "No TOSEC images found matching your search.";
                EmptyText.IsVisible = DisplayedGames.Count == 0;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            EmptyText.Text = $"TOSEC search error: {ex.Message}";
            EmptyText.IsVisible = true;
        }
        finally
        {
            LoadingPanel.IsVisible = false;
        }
    }

    private async Task PerformArchiveSearchAsync()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        LoadingPanel.IsVisible = true;
        EmptyText.IsVisible = false;
        DisplayedGames.Clear();

        try
        {
            string query = TextSearch.Text?.Trim() ?? "";
            Config.ConfigOptions.RunninConfig.LastDownloaderSearch = TextSearch.Text ?? "";
            Program.Config.DumpJsonConfig();
            string libPath = Config.ConfigOptions.RunninConfig.LibraryPath;

            var results = await _catalogService.SearchArchiveOrgAsync(query, libPath, token);

            if (!token.IsCancellationRequested)
            {
                foreach (var game in results)
                    DisplayedGames.Add(game);

                EmptyText.Text = "No results found on Internet Archive for your query.";
                EmptyText.IsVisible = DisplayedGames.Count == 0;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            EmptyText.Text = $"Search error: {ex.Message}";
            EmptyText.IsVisible = true;
        }
        finally
        {
            LoadingPanel.IsVisible = false;
        }
    }

    private async void OnGameActionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not DownloadableGame game)
            return;

        string libPath = Config.ConfigOptions.RunninConfig.LibraryPath;

        if (string.IsNullOrWhiteSpace(libPath))
        {
            TinyDialogs.MessageBox(
                "Library Not Configured",
                "Please configure your library folder before downloading games.\nGo to: File -> Configure Library.",
                MessageBoxDialogType.Ok,
                MessageBoxIconType.Warning,
                MessageBoxButton.Ok);
            return;
        }

        // If Archive.org game, resolve best direct disk URL first
        if (game.Source == "Internet Archive")
        {
            game.Status = DownloadStatus.Downloading;
            game.StatusMessage = "Resolving disk image...";
            string resolvedUrl = await _catalogService.ResolveArchiveOrgDownloadUrlAsync(game.Id);
            game.DownloadUrl = resolvedUrl;
            game.FileName = Path.GetFileName(new Uri(resolvedUrl).LocalPath);
        }

        bool success = await _downloadService.DownloadGameAsync(game, libPath);

        if (!success && !string.IsNullOrEmpty(game.StatusMessage))
        {
            TinyDialogs.MessageBox(
                "Download Failed",
                $"Could not download '{game.Title}':\n{game.StatusMessage}",
                MessageBoxDialogType.Ok,
                MessageBoxIconType.Error,
                MessageBoxButton.Ok);
        }
    }

    private void OnPlayGameClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not DownloadableGame game)
            return;

        string diskPath = !string.IsNullOrEmpty(game.LocalPath) && File.Exists(game.LocalPath)
            ? game.LocalPath
            : null;

        string libPath = Config.ConfigOptions.RunninConfig.LibraryPath;

        if (string.IsNullOrEmpty(diskPath) && !string.IsNullOrWhiteSpace(libPath) && Directory.Exists(libPath))
        {
            string candidate = Path.Combine(libPath, game.FileName);
            if (File.Exists(candidate))
            {
                diskPath = candidate;
            }
            else
            {
                // Search fallback in library directory
                diskPath = Directory.EnumerateFiles(libPath, $"{game.Id}.*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true }).FirstOrDefault();
            }
        }

        if (string.IsNullOrEmpty(diskPath))
        {
            string tosecPath = Config.GetDefaultTosecPath();
            if (!string.IsNullOrWhiteSpace(tosecPath) && Directory.Exists(tosecPath))
            {
                diskPath = Directory.EnumerateFiles(tosecPath, $"{game.Id}.*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true }).FirstOrDefault();
            }
        }

        if (!string.IsNullOrEmpty(diskPath) && File.Exists(diskPath))
        {
            SelectedDiskPath = diskPath;
            Close(diskPath);
        }
        else
        {
            TinyDialogs.MessageBox(
                "File Not Found",
                $"The disk image for '{game.Title}' was not found locally.\nPlease download it or select a different game.",
                MessageBoxDialogType.Ok,
                MessageBoxIconType.Warning,
                MessageBoxButton.Ok);
        }
    }

    private async void OnUninstallGameClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not DownloadableGame game)
            return;

        string libPath = Config.ConfigOptions.RunninConfig.LibraryPath;
        if (string.IsNullOrWhiteSpace(libPath))
            return;

        var result = await Dialogs.MessageBox(
            "Uninstall Game",
            $"Are you sure you want to uninstall and delete '{game.Title}'?\nThis will remove the downloaded disk image and media from your library.",
            MessageBoxDialogType.OkCancel,
            MessageBoxIconType.Warning,
            MessageBoxButton.Cancel);

        if (result == MessageBoxButton.Ok)
        {
            GameDownloadService.UninstallGame(game.FileName, game.Id, libPath);
            game.Status = DownloadStatus.NotDownloaded;
            game.StatusMessage = "";
            game.Progress = 0;
        }
    }

    private async void OnDownloadDirectClick(object sender, RoutedEventArgs e)
    {
        string url = TextDirectUrl.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            TinyDialogs.MessageBox(
                "Invalid URL",
                "Please enter a valid HTTP or HTTPS URL to an Atari ST disk image or archive (.st, .msa, .zip).",
                MessageBoxDialogType.Ok,
                MessageBoxIconType.Warning,
                MessageBoxButton.Ok);
            return;
        }

        string libPath = Config.ConfigOptions.RunninConfig.LibraryPath;
        if (string.IsNullOrWhiteSpace(libPath))
        {
            TinyDialogs.MessageBox(
                "Library Not Configured",
                "Please configure your library folder before downloading.\nGo to: File -> Configure Library.",
                MessageBoxDialogType.Ok,
                MessageBoxIconType.Warning,
                MessageBoxButton.Ok);
            return;
        }

        string fileName = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "downloaded_game.st";

        var game = new DownloadableGame
        {
            Id = Path.GetFileNameWithoutExtension(fileName),
            Title = Path.GetFileNameWithoutExtension(fileName),
            DownloadUrl = url,
            FileName = fileName,
            Category = "Direct Download",
            Source = "Direct Link",
            Description = $"Downloaded from: {url}"
        };

        DisplayedGames.Insert(0, game);
        await _downloadService.DownloadGameAsync(game, libPath);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
