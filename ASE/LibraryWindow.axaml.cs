using ASE.Models;
using ASE.Services;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibVLCSharp.Shared;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using TinyDialogsNet;

namespace ASE;

public partial class LibraryWindow : Window
{
    public LibraryCollection libraryCollection { get; set; } = new LibraryCollection();

    /// <summary>Entries currently shown in the grid (search-filtered view over the full library).
    /// Closes with the full path of the game to insert as the dialog result, or null.</summary>
    public ObservableCollection<GameEntry> Games { get; } = new();

    /// <summary>Catalogue entry the dialog closed with, beside the disk-image path it
    /// returns: MainWindow needs the entry itself to apply the game's MT-32 instrument
    /// profile (see <see cref="MT32.Mt32Profiles"/>). Null when nothing was launched.</summary>
    public LibraryItem SelectedGame { get; private set; }

    readonly List<GameEntry> _allGames = new();
    CancellationTokenSource _coversCts;
    private string _libraryPath;

    public int CurrentPage { get; private set; } = 1;
    public const int PageSize = 300;
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)_filteredGames.Count / PageSize));
    private List<GameEntry> _filteredGames = new();
    private CancellationTokenSource _pageScrapeCts;

    /// <summary>Tile currently under the mouse pointer; Enter launches it over the selection.</summary>
    GameEntry _hovered;

    // Detail overlay state. LibVLC is created lazily on the first video and reused;
    // it stays null when libvlc isn't available (the panel then shows the screenshot).
    LibVLC _libVlc;
    MediaPlayer _mediaPlayer;
    GameEntry _detailGame;
    bool _detailOpen;

    public LibraryWindow() : this(null) { }

    public LibraryWindow(string initialPath)
    {
        InitializeComponent();

        // Park the emulator (CPU + audio) while browsing: the PSG output would
        // otherwise mix with the game videos. Resumed in the Closed handler.
        ASEMain.EnterUiPause();

        // The ListBox itself handles Enter (confirms the focused item's selection) and
        // marks it Handled before XAML-subscribed handlers run, so the Enter case never
        // fired when subscribed via KeyDown="..."; listen with handledEventsToo instead
        GamesList.AddHandler(KeyDownEvent, OnGamesKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);

        _libraryPath = !string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath)
            ? initialPath
            : (Config.ConfigOptions.RunninConfig.LibraryPath ?? "");
        DataContext = this;

        if (!string.IsNullOrEmpty(Config.ConfigOptions.RunninConfig.LastLibrarySearch))
            SearchBox.Text = Config.ConfigOptions.RunninConfig.LastLibrarySearch;

        UpdateTosecButtonState();
        ReloadLibrary();
        GameDownloadService.GameInstalled += OnExternalGameInstalled;
        GameDownloadService.GameUninstalled += OnExternalGameUninstalled;

        Closed += (s, e) =>
        {
            GameDownloadService.GameInstalled -= OnExternalGameInstalled;
            GameDownloadService.GameUninstalled -= OnExternalGameUninstalled;
            ASEMain.ExitUiPause();
            _coversCts?.Cancel();
            _coversCts?.Dispose();
            _coversCts = null;
            _pageScrapeCts?.Cancel();
            _pageScrapeCts?.Dispose();
            _pageScrapeCts = null;

            // Unhook the video host before disposing: Avalonia destroys the native
            // control deferred, after this handler, and it must not touch a disposed player
            var player = _mediaPlayer;
            _mediaPlayer = null;
            DetailVideo.MediaPlayer = null;
            player?.Dispose();
            _libVlc?.Dispose();
        };
        Opened += (s, e) => Dispatcher.UIThread.Post(FocusSelected, DispatcherPriority.Loaded);
    }

    private void OnExternalGameInstalled(string fileName) => Dispatcher.UIThread.Post(ReloadLibrary);
    private void OnExternalGameUninstalled(string fileName) => Dispatcher.UIThread.Post(ReloadLibrary);

    private string GetTosecRoot()
    {
        string configured = Config.ConfigOptions.RunninConfig.TosecPath;
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            var dir = new DirectoryInfo(configured);
            while (dir != null)
            {
                if (dir.Name.Contains("TOSEC", StringComparison.OrdinalIgnoreCase))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return configured;
        }

        string defaultRoot = @"C:\Users\adria\Desktop\Atari Emulator\Atari ST [TOSEC]";
        if (Directory.Exists(defaultRoot))
            return defaultRoot;

        string eRoot = @"E:\Atari ST [TOSEC]";
        if (Directory.Exists(eRoot))
            return eRoot;

        return Config.GetDefaultTosecPath();
    }

    private string FindTosecSubfolder(string parent, string searchPattern)
    {
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            return null;

        try
        {
            var match = Directory.EnumerateDirectories(parent, searchPattern, SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (match != null) return match;

            foreach (var sub in Directory.EnumerateDirectories(parent))
            {
                var subMatch = Directory.EnumerateDirectories(sub, searchPattern, SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (subMatch != null) return subMatch;
            }
        }
        catch { }

        return null;
    }

    private void UpdateTosecButtonState()
    {
        if (TextTosecButton == null) return;
        string defaultLib = Config.ConfigOptions.RunninConfig.LibraryPath ?? "";
        string current = _libraryPath ?? "";

        if (!string.IsNullOrEmpty(defaultLib) && current.Equals(defaultLib, StringComparison.OrdinalIgnoreCase))
        {
            TextTosecButton.Text = "Standard Lib ▾";
        }
        else if (current.IndexOf("Games - [STX]", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            TextTosecButton.Text = "TOSEC: STX ▾";
        }
        else if (current.IndexOf("Games - [ST]", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            TextTosecButton.Text = "TOSEC: ST ▾";
        }
        else if (current.IndexOf("Public Domain", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            TextTosecButton.Text = "TOSEC: PD ▾";
        }
        else if (current.EndsWith("Games", StringComparison.OrdinalIgnoreCase))
        {
            TextTosecButton.Text = "TOSEC: All Games ▾";
        }
        else if (current.IndexOf("Demos", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            TextTosecButton.Text = "TOSEC: Demos ▾";
        }
        else if (current.IndexOf("Compilations", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            TextTosecButton.Text = "TOSEC: Compilations ▾";
        }
        else if (current.IndexOf("Coverdisks", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            TextTosecButton.Text = "TOSEC: Coverdisks ▾";
        }
        else if (current.IndexOf("Applications", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            TextTosecButton.Text = "TOSEC: Apps ▾";
        }
        else if (current.Contains("TOSEC", StringComparison.OrdinalIgnoreCase))
        {
            TextTosecButton.Text = "TOSEC ▾";
        }
        else
        {
            string folderName = Path.GetFileName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            TextTosecButton.Text = (string.IsNullOrEmpty(folderName) ? "Library" : folderName) + " ▾";
        }

        if (ButtonTosecFolder != null)
        {
            ToolTip.SetTip(ButtonTosecFolder, $"Current collection:\n{current}\n(Click to switch collection or category)");
        }
    }

    private void OnMenuSelectStandardLib(object sender, RoutedEventArgs e)
    {
        string defaultLib = Config.ConfigOptions.RunninConfig.LibraryPath ?? "";
        if (!string.IsNullOrEmpty(defaultLib) && Directory.Exists(defaultLib))
        {
            SwitchLibraryPath(defaultLib);
        }
        else
        {
            string defaultDir = Path.Combine(Config.GetAppDefaultConfigsFilePath(), "Games");
            if (Directory.Exists(defaultDir))
                SwitchLibraryPath(defaultDir);
        }
    }

    private void OnMenuSelectTosecAllGames(object sender, RoutedEventArgs e)
    {
        string root = GetTosecRoot();
        string gamesDir = Path.Combine(root, "Games");
        if (Directory.Exists(gamesDir))
        {
            SwitchLibraryPath(gamesDir);
        }
        else if (Directory.Exists(root))
        {
            SwitchLibraryPath(root);
        }
    }

    private void OnMenuSelectTosecSt(object sender, RoutedEventArgs e)
    {
        string root = GetTosecRoot();
        string stDir = FindTosecSubfolder(Path.Combine(root, "Games"), "*Games - [ST]*") 
                    ?? FindTosecSubfolder(root, "*Games - [ST]*");
        if (stDir != null && Directory.Exists(stDir))
        {
            SwitchLibraryPath(stDir);
        }
        else
        {
            OnMenuSelectTosecAllGames(sender, e);
        }
    }

    private void OnMenuSelectTosecStx(object sender, RoutedEventArgs e)
    {
        string root = GetTosecRoot();
        string stxDir = FindTosecSubfolder(Path.Combine(root, "Games"), "*Games - [STX]*") 
                     ?? FindTosecSubfolder(root, "*Games - [STX]*");
        if (stxDir != null && Directory.Exists(stxDir))
        {
            SwitchLibraryPath(stxDir);
        }
        else
        {
            OnMenuSelectTosecAllGames(sender, e);
        }
    }

    private void OnMenuSelectTosecPd(object sender, RoutedEventArgs e)
    {
        string root = GetTosecRoot();
        string pdDir = FindTosecSubfolder(Path.Combine(root, "Games"), "*Public Domain*") 
                    ?? FindTosecSubfolder(root, "*Public Domain*");
        if (pdDir != null && Directory.Exists(pdDir))
        {
            SwitchLibraryPath(pdDir);
        }
        else
        {
            OnMenuSelectTosecAllGames(sender, e);
        }
    }

    private void OnMenuSelectTosecDemos(object sender, RoutedEventArgs e)
    {
        string root = GetTosecRoot();
        string demosDir = FindTosecSubfolder(root, "*Demos*") ?? Path.Combine(root, "Demos");
        if (Directory.Exists(demosDir))
        {
            SwitchLibraryPath(demosDir);
        }
    }

    private void OnMenuSelectTosecCompilations(object sender, RoutedEventArgs e)
    {
        string root = GetTosecRoot();
        string compDir = FindTosecSubfolder(root, "*Compilations*") ?? Path.Combine(root, "Compilations");
        if (Directory.Exists(compDir))
        {
            SwitchLibraryPath(compDir);
        }
    }

    private void OnMenuSelectTosecCoverdisks(object sender, RoutedEventArgs e)
    {
        string root = GetTosecRoot();
        string cdDir = FindTosecSubfolder(root, "*Coverdisks*") ?? Path.Combine(root, "Coverdisks");
        if (Directory.Exists(cdDir))
        {
            SwitchLibraryPath(cdDir);
        }
    }

    private void OnMenuSelectTosecApplications(object sender, RoutedEventArgs e)
    {
        string root = GetTosecRoot();
        string appsDir = FindTosecSubfolder(root, "*Applications*") ?? Path.Combine(root, "Applications");
        if (Directory.Exists(appsDir))
        {
            SwitchLibraryPath(appsDir);
        }
    }

    private async void OnMenuBrowseOtherFolder(object sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider != null)
        {
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "Select Games or TOSEC Folder",
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                string chosen = folders[0].Path.LocalPath;
                if (Directory.Exists(chosen))
                {
                    Config.ConfigOptions.RunninConfig.TosecPath = chosen;
                    Program.Config.DumpJsonConfig();
                    SwitchLibraryPath(chosen);
                }
            }
        }
    }

    public void SwitchLibraryPath(string newPath)
    {
        if (string.IsNullOrWhiteSpace(newPath) || !Directory.Exists(newPath))
            return;

        _libraryPath = newPath;
        UpdateTosecButtonState();
        ReloadLibrary();
    }

    private static string GetLibraryCachePath(string libPath)
    {
        try
        {
            return Path.Combine(libPath, ".ase_library_cache.json");
        }
        catch
        {
            string appDataCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ASE", "cache");
            if (!Directory.Exists(appDataCache)) Directory.CreateDirectory(appDataCache);
            return Path.Combine(appDataCache, $"cache_{Math.Abs(libPath.GetHashCode()):X8}.json");
        }
    }

    public void ReloadLibrary()
    {
        _coversCts?.Cancel();
        _coversCts?.Dispose();
        _coversCts = new CancellationTokenSource();
        var token = _coversCts.Token;

        _allGames.Clear();
        libraryCollection = new LibraryCollection();

        string libraryJson = Path.Combine(_libraryPath, "Library.json");

        if (_libraryPath.Length > 0 && File.Exists(libraryJson))
        {
            try
            {
                libraryCollection = JsonSerializer.Deserialize<LibraryCollection>(File.ReadAllText(libraryJson)) ?? new LibraryCollection();
            }
            catch { }
        }

        if (_libraryPath.Length > 0 && Directory.Exists(_libraryPath))
        {
            bool loadedFromCache = false;
            string cachePath = GetLibraryCachePath(_libraryPath);

            try
            {
                long currentDirTicks = Directory.GetLastWriteTimeUtc(_libraryPath).Ticks;
                if (File.Exists(cachePath))
                {
                    string cacheJson = File.ReadAllText(cachePath);
                    var cacheData = JsonSerializer.Deserialize<LibraryCacheData>(cacheJson);
                    if (cacheData != null && cacheData.DirectoryTicks == currentDirTicks && cacheData.Items?.Count > 0)
                    {
                        libraryCollection.Collection = cacheData.Items;
                        loadedFromCache = true;
                    }
                }
            }
            catch { }

            if (!loadedFromCache)
            {
                // Also discover and merge any subfolder Library.json files (e.g. TOSEC subcollections)
                try
                {
                    var subJsonFiles = Directory.EnumerateFiles(_libraryPath, "Library.json", new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        MaxRecursionDepth = 3,
                        IgnoreInaccessible = true
                    });

                    foreach (var jf in subJsonFiles)
                    {
                        if (string.Equals(jf, libraryJson, StringComparison.OrdinalIgnoreCase))
                            continue;

                        try
                        {
                            var subCol = JsonSerializer.Deserialize<LibraryCollection>(File.ReadAllText(jf));
                            if (subCol?.Collection != null)
                            {
                                string subDir = Path.GetDirectoryName(jf);
                                libraryCollection.Collection ??= new List<LibraryItem>();
                                foreach (var item in subCol.Collection)
                                {
                                    if (!string.IsNullOrEmpty(item.Filename) && !Path.IsPathRooted(item.Filename))
                                    {
                                        string full = Path.Combine(subDir, item.Filename);
                                        item.Filename = Path.GetRelativePath(_libraryPath, full);
                                    }
                                    if (!libraryCollection.Collection.Any(x => x.Id == item.Id || x.Filename == item.Filename))
                                        libraryCollection.Collection.Add(item);
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                var diskExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".st", ".msa", ".stx", ".dim", ".ipf", ".zip" };
                libraryCollection.Collection ??= new List<LibraryItem>();
                var knownFiles = new HashSet<string>(libraryCollection.Collection.Where(i => !string.IsNullOrEmpty(i.Filename)).Select(i => i.Filename), StringComparer.OrdinalIgnoreCase);

                try
                {
                    var searchOptions = new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        MaxRecursionDepth = 3,
                        IgnoreInaccessible = true
                    };

                    foreach (var file in Directory.EnumerateFiles(_libraryPath, "*.*", searchOptions))
                    {
                        string ext = Path.GetExtension(file);
                        if (diskExtensions.Contains(ext))
                        {
                            string fname = Path.GetFileName(file);
                            string relPath = Path.GetRelativePath(_libraryPath, file);
                            if (!knownFiles.Contains(fname) && !knownFiles.Contains(relPath))
                            {
                                libraryCollection.Collection.Add(new LibraryItem
                                {
                                    Id = Path.GetFileNameWithoutExtension(fname),
                                    Filename = relPath,
                                    Name = new List<LibraryItem.RegionText>
                                    {
                                        new() { Region = "ss", Text = Path.GetFileNameWithoutExtension(fname).Replace('_', ' ') }
                                    }
                                });
                                knownFiles.Add(fname);
                                knownFiles.Add(relPath);
                            }
                        }
                    }
                }
                catch { }

                // Save cache in background
                var itemsToCache = libraryCollection.Collection.ToList();
                long dirTicks = Directory.GetLastWriteTimeUtc(_libraryPath).Ticks;
                Task.Run(() =>
                {
                    try
                    {
                        var data = new LibraryCacheData { DirectoryTicks = dirTicks, Items = itemsToCache };
                        File.WriteAllText(cachePath, JsonSerializer.Serialize(data));
                    }
                    catch { }
                });
            }
        }

        // Build media index once for fast in-memory lookups
        var mediaDirs = new List<string>();
        string mainMedia = Path.Combine(_libraryPath, "Media");
        if (Directory.Exists(mainMedia))
            mediaDirs.Add(mainMedia);

        try
        {
            var subMedia = Directory.EnumerateDirectories(_libraryPath, "Media", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 3,
                IgnoreInaccessible = true
            });
            foreach (var sm in subMedia)
            {
                if (!mediaDirs.Contains(sm, StringComparer.OrdinalIgnoreCase))
                    mediaDirs.Add(sm);
            }
        }
        catch { }

        string defaultStxMedia = Path.Combine(Config.GetDefaultTosecPath(), "Atari ST - Games - [STX] (TOSEC-v2011-03-20_CM)", "Media");
        if (Directory.Exists(defaultStxMedia) && !mediaDirs.Contains(defaultStxMedia, StringComparer.OrdinalIgnoreCase))
            mediaDirs.Add(defaultStxMedia);

        var mediaIndex = MediaIndex.Build(mediaDirs);

        if (libraryCollection.Collection != null)
        {
            _allGames.AddRange(libraryCollection.Collection
                .Select(item => new GameEntry(item, mediaIndex))
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase));
        }

        ApplyFilter();
    }

    private async void OnDownloadGamesClick(object sender, RoutedEventArgs e)
    {
        var dlWindow = new DownloadGamesWindow();
        var result = await dlWindow.ShowDialog<string>(this);
        ReloadLibrary();

        if (!string.IsNullOrEmpty(result))
        {
            var game = _allGames.FirstOrDefault(g =>
                string.Equals(g.Item.Filename, Path.GetFileName(result), StringComparison.OrdinalIgnoreCase));
            if (game != null)
                LoadGame(game);
            else
                Close(result);
        }
    }

    private void OnRefreshLibraryClick(object sender, RoutedEventArgs e)
    {
        ReloadLibrary();
    }

    void LoadCovers(GameEntry[] games, CancellationToken ct)
    {
        try
        {
            foreach (var game in games)
            {
                if (ct.IsCancellationRequested)
                    return;

                if (game.CoverPath == null || game.Cover != null)
                    continue;

                try
                {
                    // 240 px ≈ the 160 px tile content at 150% scaling; keeps memory at
                    // ~250 KB per cover, which is what dominates with big libraries
                    Bitmap cover;
                    using (var stream = File.OpenRead(game.CoverPath))
                        cover = Bitmap.DecodeToWidth(stream, 240);

                    Dispatcher.UIThread.Post(() => game.Cover = cover);
                }
                catch
                {
                    // Unreadable/corrupt image: the tile keeps its placeholder
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ColoredConsole.WriteLine($"[[yellow]]Cover loader handled exception: {ex.Message}[[/yellow]]");
        }
    }

    void ApplyFilter()
    {
        string filter = SearchBox.Text?.Trim() ?? "";

        _filteredGames = filter.Length == 0
            ? _allGames
            : _allGames.Where(g => g.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        CurrentPage = 1;
        UpdatePageDisplay();
    }

    void UpdatePageDisplay(int preferredSelectionIndex = 0)
    {
        _coversCts?.Cancel();
        _coversCts?.Dispose();
        _coversCts = new CancellationTokenSource();
        var token = _coversCts.Token;

        int totalPages = TotalPages;
        if (CurrentPage > totalPages) CurrentPage = totalPages;
        if (CurrentPage < 1) CurrentPage = 1;

        int startIdx = (CurrentPage - 1) * PageSize;
        var pageSlice = _filteredGames.Skip(startIdx).Take(PageSize).ToList();

        _hovered = null;
        Games.Clear();
        foreach (var game in pageSlice)
            Games.Add(game);

        if (TextCollectionCount != null)
            TextCollectionCount.Text = $"{_allGames.Count:N0} games in library";

        if (TextPageInfo != null)
            TextPageInfo.Text = $"Page {CurrentPage} of {totalPages}";

        if (TextPageRange != null)
        {
            int endIdx = Math.Min(startIdx + PageSize, _filteredGames.Count);
            TextPageRange.Text = _filteredGames.Count > 0 ? $"{startIdx + 1}–{endIdx} of {_filteredGames.Count:N0}" : "0 games";
        }

        if (ButtonFirstPage != null) ButtonFirstPage.IsEnabled = CurrentPage > 1;
        if (ButtonPrevPage != null) ButtonPrevPage.IsEnabled = CurrentPage > 1;
        if (ButtonNextPage != null) ButtonNextPage.IsEnabled = CurrentPage < totalPages;
        if (ButtonLastPage != null) ButtonLastPage.IsEnabled = CurrentPage < totalPages;
        if (ButtonScrapePage != null) ButtonScrapePage.IsEnabled = pageSlice.Any(g => !g.HasCover);

        if (Games.Count > 0)
        {
            int selIdx = Math.Clamp(preferredSelectionIndex, 0, Games.Count - 1);
            GamesList.SelectedIndex = selIdx;
        }

        EmptyMessage.Text = _allGames.Count == 0
            ? "🤷‍♂️ The library is empty. Click 'Download Games…' or place disk images in your library folder."
            : "❌ No games match the filter.";
        EmptyPanel.IsVisible = Games.Count == 0;
        ButtonEmptyDownload.IsVisible = _allGames.Count == 0;

        var pageSnapshot = pageSlice.ToArray();
        Task.Run(() => LoadCovers(pageSnapshot, token));
    }

    void FocusSelected()
    {
        if (GamesList.SelectedIndex < 0 && Games.Count > 0)
            GamesList.SelectedIndex = 0;

        if (GamesList.SelectedIndex >= 0 && GamesList.ContainerFromIndex(GamesList.SelectedIndex) is Control container)
            container.Focus();
        else
            GamesList.Focus();
    }

    void LoadSelected() => LoadGame(GamesList.SelectedItem as GameEntry);

    async void LoadGame(GameEntry game)
    {
        if (game == null || string.IsNullOrEmpty(game.Item.Filename))
            return;

        string path = Path.IsPathRooted(game.Item.Filename)
            ? game.Item.Filename
            : Path.Combine(_libraryPath, game.Item.Filename);

        if (!File.Exists(path))
        {
            await Dialogs.MessageBox("Error", $"Game file not found: {path}",
                MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
            return;
        }

        SelectedGame = game.Item;
        Close(path);
    }

    /// <summary>Items per row in the wrap grid, derived from the realized tile and panel widths.</summary>
    int ColumnsPerRow()
    {
        if (GamesList.ContainerFromIndex(0) is not Control tile || tile.Bounds.Width <= 0)
            return 1;

        double tileWidth = tile.Bounds.Width + tile.Margin.Left + tile.Margin.Right;
        double panelWidth = GamesList.ItemsPanelRoot?.Bounds.Width ?? GamesList.Bounds.Width;

        return Math.Max(1, (int)((panelWidth + 0.5) / tileWidth));
    }

    // The horizontal WrapPanel only navigates Left/Right on its own; Up/Down jump a whole row here
    void MoveSelectionVertical(int direction)
    {
        if (Games.Count == 0)
            return;

        int index = GamesList.SelectedIndex;

        if (index < 0)
        {
            GamesList.SelectedIndex = 0;
            FocusSelected();
            return;
        }

        int columns = ColumnsPerRow();
        int target = index + direction * columns;

        if (direction > 0 && target >= Games.Count)
        {
            // From the last full row, land on the last game; from the last row, stay put
            if (index / columns == (Games.Count - 1) / columns)
                return;
            target = Games.Count - 1;
        }
        else if (target < 0)
            return;

        GamesList.SelectedIndex = target;
        FocusSelected();
    }

    void OnDetailsTapped(object sender, TappedEventArgs e)
    {
        // Keep the tap from bubbling into OnGamesTapped, which would launch the game
        e.Handled = true;

        if ((sender as Control)?.DataContext is GameEntry game)
            OpenDetail(game);
    }

    /// <summary>Context click on a tile: right button, or Ctrl+click on macOS — the
    /// one-button/trackpad secondary click, which Avalonia does not translate into a
    /// right click (AvaloniaUI/Avalonia#5575), so the menu is opened from code there.</summary>
    void OnTilePointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (sender is not Border tile || tile.DataContext is not GameEntry game)
            return;

        var props = e.GetCurrentPoint(tile).Properties;
        bool ctrlClick = OperatingSystem.IsMacOS() && props.IsLeftButtonPressed &&
                         e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (!props.IsRightButtonPressed && !ctrlClick)
            return;

        // The menu must act on the tile under the cursor, not on a stale selection
        GamesList.SelectedItem = game;

        if (ctrlClick)
        {
            // Swallow the press so its Tapped never reaches OnGamesTapped (launch);
            // the IsOpen guard keeps this correct if Avalonia ever translates the click
            e.Handled = true;
            if (tile.ContextMenu is { IsOpen: false } menu)
                menu.Open(tile);
        }
    }

    void OnContextPlayClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is GameEntry game)
            LoadGame(game);
    }

    void OnContextDetailsClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is GameEntry game)
            OpenDetail(game);
    }

    async void OnContextUninstallClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is GameEntry game)
            await PromptUninstallGameAsync(game);
    }

    async void OnContextChangeArtworkClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not GameEntry game)
            return;

        string mediaDir = GetItemMediaDir(game.Item);
        string targetBoxPath = Path.Combine(mediaDir, $"Box-{game.Item.Id}.png");

        var picker = new ArtworkPickerWindow(game.Name, game.Item.Id, targetBoxPath, mediaDir);
        bool? result = await picker.ShowDialog<bool?>(this);

        if (result == true)
        {
            if (picker.ResultRemoveCover)
            {
                game.Cover = null;
                game.CoverPath = null;
            }
            else if (!string.IsNullOrEmpty(picker.ResultCoverPath) && File.Exists(picker.ResultCoverPath))
            {
                game.CoverPath = picker.ResultCoverPath;
                try
                {
                    using var stream = File.OpenRead(picker.ResultCoverPath);
                    game.Cover = Bitmap.DecodeToWidth(stream, 240);
                }
                catch { }
            }
        }
    }

    async void OnContextChooseImageClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not GameEntry game)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider != null)
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = $"Select Cover Artwork for {game.Name}",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Image Files")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.gif" }
                    }
                }
            });

            if (files.Count > 0)
            {
                string localPath = files[0].Path.LocalPath;
                string mediaDir = GetItemMediaDir(game.Item);
                string targetBoxPath = Path.Combine(mediaDir, $"Box-{game.Item.Id}.png");

                try
                {
                    File.Copy(localPath, targetBoxPath, true);
                    game.CoverPath = targetBoxPath;
                    using var stream = File.OpenRead(targetBoxPath);
                    game.Cover = Bitmap.DecodeToWidth(stream, 240);
                }
                catch (Exception ex)
                {
                    TinyDialogs.MessageBox("Error", $"Could not update cover: {ex.Message}", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                }
            }
        }
    }

    void OnContextRemoveArtworkClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not GameEntry game)
            return;

        string mediaDir = GetItemMediaDir(game.Item);
        try
        {
            foreach (var f in Directory.EnumerateFiles(mediaDir, $"Box-{game.Item.Id}.*"))
            {
                try { File.Delete(f); } catch { }
            }
            game.Cover = null;
            game.CoverPath = null;
        }
        catch { }
    }

    private async void OnScrapeMissingArtworkClick(object sender, RoutedEventArgs e)
    {
        var missingGames = _allGames.Where(g => !g.HasCover).ToList();
        if (missingGames.Count == 0)
        {
            await Dialogs.MessageBox("Artwork Up to Date", "All games in this library currently have cover artwork!", MessageBoxDialogType.Ok, MessageBoxIconType.Information, MessageBoxButton.Ok);
            return;
        }

        var choice = await Dialogs.MessageBox(
            "Scrape Missing Artwork",
            $"Found {missingGames.Count} game(s) without covers in this library.\n\n" +
            "Would you like to automatically scrape authentic box art and title screens from the Internet Archive without needing ScreenScraper developer keys?",
            MessageBoxDialogType.YesNo,
            MessageBoxIconType.Question,
            MessageBoxButton.Yes);

        if (choice == MessageBoxButton.Yes)
        {
            var scraperWin = new ScraperWindow(_libraryPath);
            await scraperWin.ShowDialog(this);
            ReloadLibrary();
        }
    }

    private async void OnScrapeCurrentPageClick(object sender, RoutedEventArgs e)
    {
        if (_pageScrapeCts != null && !_pageScrapeCts.IsCancellationRequested)
        {
            _pageScrapeCts.Cancel();
            if (TextScrapePageBtn != null)
                TextScrapePageBtn.Text = "Stopping…";
            return;
        }

        var missingOnPage = Games.Where(g => !g.HasCover).ToList();
        if (missingOnPage.Count == 0)
        {
            await Dialogs.MessageBox("Artwork Up to Date", "All games on this page already have cover artwork!", MessageBoxDialogType.Ok, MessageBoxIconType.Information, MessageBoxButton.Ok);
            return;
        }

        var choice = await Dialogs.MessageBox(
            "Scrape Page Artwork",
            $"Scrape online box art and title screens for {missingOnPage.Count} game(s) on this page from Libretro CDN and the Internet Archive?\n\nYou can click the Stop button at any time during scraping to cancel.",
            MessageBoxDialogType.YesNo,
            MessageBoxIconType.Question,
            MessageBoxButton.Yes);

        if (choice != MessageBoxButton.Yes)
            return;

        _pageScrapeCts?.Cancel();
        _pageScrapeCts?.Dispose();
        _pageScrapeCts = new CancellationTokenSource();
        var ct = _pageScrapeCts.Token;

        int scrapedCount = 0;
        var scraper = new ArtworkScraperService();

        try
        {
            for (int i = 0; i < missingOnPage.Count; i++)
            {
                if (ct.IsCancellationRequested)
                    break;

                var game = missingOnPage[i];
                if (TextScrapePageBtn != null)
                    TextScrapePageBtn.Text = $"⏹ Stop ({i + 1}/{missingOnPage.Count})";

                string mediaDir = GetItemMediaDir(game.Item);
                try
                {
                    var candidates = await scraper.SearchCandidatesAsync(game.Name, game.Item.Id, mediaDir, ct);
                    var bestCandidate = candidates.FirstOrDefault(c => c.Badge.Contains("Cover") || c.Badge.Contains("Box") || c.Badge.Contains("Title") || c.Badge.Contains("Screen") || c.Badge.Contains("Thumb") || c.Badge.Contains("Snap"));

                    if (bestCandidate != null)
                    {
                        string targetBoxPath = Path.Combine(mediaDir, $"Box-{game.Item.Id}.png");
                        bool saved = await scraper.DownloadAndSaveArtworkAsync(bestCandidate, targetBoxPath, ct);
                        if (saved && File.Exists(targetBoxPath))
                        {
                            game.CoverPath = targetBoxPath;
                            try
                            {
                                using var stream = File.OpenRead(targetBoxPath);
                                game.Cover = Bitmap.DecodeToWidth(stream, 240);
                                scrapedCount++;
                            }
                            catch { }

                            // Also save under clean title so identical games across TOSEC or multi-disks share the artwork
                            string cleanTitle = ArtworkScraperService.CleanGameTitle(game.Name);
                            if (!string.IsNullOrEmpty(cleanTitle))
                            {
                                string cleanBoxPath = Path.Combine(mediaDir, $"Box-{cleanTitle}.png");
                                if (!cleanBoxPath.Equals(targetBoxPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    try { File.Copy(targetBoxPath, cleanBoxPath, true); } catch { }
                                }
                                string underBox = Path.Combine(mediaDir, $"Box-{cleanTitle.Replace(' ', '_')}.png");
                                if (!underBox.Equals(targetBoxPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    try { File.Copy(targetBoxPath, underBox, true); } catch { }
                                }
                            }
                        }
                    }

                    await Task.Delay(100, ct); // Fast and polite
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch { }
            }
        }
        finally
        {
            _pageScrapeCts?.Dispose();
            _pageScrapeCts = null;

            if (TextScrapePageBtn != null)
                TextScrapePageBtn.Text = "Scrape Page Artwork";
            if (ButtonScrapePage != null)
                ButtonScrapePage.IsEnabled = Games.Any(g => !g.HasCover);
        }

        if (scrapedCount > 0)
        {
            await Dialogs.MessageBox("Scraping Complete", $"Successfully downloaded artwork for {scrapedCount} game(s) on this page!", MessageBoxDialogType.Ok, MessageBoxIconType.Information, MessageBoxButton.Ok);
        }
    }

    private void OnFirstPageClick(object sender, RoutedEventArgs e)
    {
        if (CurrentPage > 1)
        {
            CurrentPage = 1;
            UpdatePageDisplay();
            FocusSelected();
        }
    }

    private void OnPrevPageClick(object sender, RoutedEventArgs e)
    {
        if (CurrentPage > 1)
        {
            CurrentPage--;
            UpdatePageDisplay();
            FocusSelected();
        }
    }

    private void OnNextPageClick(object sender, RoutedEventArgs e)
    {
        if (CurrentPage < TotalPages)
        {
            CurrentPage++;
            UpdatePageDisplay();
            FocusSelected();
        }
    }

    private void OnLastPageClick(object sender, RoutedEventArgs e)
    {
        if (CurrentPage < TotalPages)
        {
            CurrentPage = TotalPages;
            UpdatePageDisplay();
            FocusSelected();
        }
    }

    private string GetItemMediaDir(LibraryItem item)
    {
        if (!string.IsNullOrEmpty(item.Filename))
        {
            string sub = Path.GetDirectoryName(item.Filename);
            if (!string.IsNullOrEmpty(sub))
            {
                string candidate = Path.IsPathRooted(sub) ? Path.Combine(sub, "Media") : Path.Combine(_libraryPath, sub, "Media");
                if (Directory.Exists(candidate))
                    return candidate;
            }
        }

        string defaultMedia = Path.Combine(_libraryPath, "Media");
        if (!Directory.Exists(defaultMedia))
            Directory.CreateDirectory(defaultMedia);
        return defaultMedia;
    }

    void OnDetailBackdropTapped(object sender, TappedEventArgs e) => CloseDetail();

    void OnDetailCloseClick(object sender, RoutedEventArgs e) => CloseDetail();

    // The Play button in the detail overlay launches the game shown, the same way
    // the context-menu "Play" and Enter do (LoadGame closes the window with its path)
    void OnDetailPlayClick(object sender, RoutedEventArgs e) => LoadGame(_detailGame);

    void OnDetailRandomClick(object sender, RoutedEventArgs e) => PickRandomGame();

    void OnRandomGameClick(object sender, RoutedEventArgs e) => PickRandomGame();

    private readonly Random _rng = new Random();

    public void PickRandomGame()
    {
        var pool = (_allGames != null && _allGames.Count > 0) ? _allGames : _filteredGames;
        if (pool == null || pool.Count == 0)
            return;

        int randomIndex = _rng.Next(pool.Count);
        var chosenGame = pool[randomIndex];

        CloseDetail();
        LoadGame(chosenGame);
    }

    async void OnDetailUninstallClick(object sender, RoutedEventArgs e)
    {
        if (_detailGame != null)
            await PromptUninstallGameAsync(_detailGame);
    }

    async Task PromptUninstallGameAsync(GameEntry game)
    {
        if (game == null)
            return;

        var result = await Dialogs.MessageBox(
            "Uninstall Game",
            $"Are you sure you want to uninstall and delete '{game.Name}'?\nThis will permanently delete the disk image and media from your library.",
            MessageBoxDialogType.OkCancel,
            MessageBoxIconType.Warning,
            MessageBoxButton.Cancel);

        if (result == MessageBoxButton.Ok)
        {
            CloseDetail();

            // Safely eject the disks if currently mounted in Drive A or Drive B
            if (!string.IsNullOrEmpty(game.Item.Filename))
            {
                if (ASEMain.driveA?.ImagePath?.Contains(game.Item.Filename, StringComparison.OrdinalIgnoreCase) == true)
                    ASEMain.driveA.Eject();
                if (ASEMain.driveB?.ImagePath?.Contains(game.Item.Filename, StringComparison.OrdinalIgnoreCase) == true)
                    ASEMain.driveB.Eject();
            }

            GameDownloadService.UninstallGame(game.Item.Filename, game.Item.Id, _libraryPath);
            // OnExternalGameUninstalled handles dispatching ReloadLibrary
        }
    }

    void OpenDetail(GameEntry game)
    {
        _detailGame = game;
        _detailOpen = true;

        DetailTitle.Text = game.Name;
        DetailRelease.Text = game.Item.GameMenuGroupRelease;
        DetailRelease.IsVisible = !string.IsNullOrEmpty(game.Item.GameMenuGroupRelease);
        DetailSynopsis.Text = game.SynopsisText ?? "No description available.";

        // Box art fills the media area until the screenshot decodes
        DetailFallback.Source = game.Cover;

        // Title screen as the card backdrop; without one the card stays solid
        DetailBackdrop.Source = null;
        if (game.TitlePath != null)
            Task.Run(() =>
            {
                try
                {
                    Bitmap backdrop;
                    using (var stream = File.OpenRead(game.TitlePath))
                        backdrop = new Bitmap(stream);

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ReferenceEquals(_detailGame, game))
                            DetailBackdrop.Source = backdrop;
                    });
                }
                catch
                {
                    // Unreadable title screen: the card keeps its solid colour
                }
            });

        if (game.ScreenshotPath != null)
            Task.Run(() =>
            {
                try
                {
                    Bitmap shot;
                    using (var stream = File.OpenRead(game.ScreenshotPath))
                        shot = Bitmap.DecodeToWidth(stream, 672);

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ReferenceEquals(_detailGame, game))
                            DetailFallback.Source = shot;
                    });
                }
                catch
                {
                    // Unreadable screenshot: the box art stays
                }
            });

        DetailOverlay.IsVisible = true;
        // The class change must land after the overlay is attached or the fade won't run
        Dispatcher.UIThread.Post(() => DetailOverlay.Classes.Add("open"), DispatcherPriority.Loaded);

        PlayVideo(game.VideoPath);
    }

    void CloseDetail()
    {
        if (!_detailOpen)
            return;

        _detailOpen = false;
        _detailGame = null;

        _mediaPlayer?.Stop();
        DetailVideo.IsVisible = false;

        DetailOverlay.Classes.Remove("open");
        // Hide only after the fade-out finishes so the transition stays visible
        DispatcherTimer.RunOnce(() =>
        {
            if (!_detailOpen)
                DetailOverlay.IsVisible = false;
        }, TimeSpan.FromMilliseconds(200));

        FocusSelected();
    }

    void PlayVideo(string path)
    {
        _mediaPlayer?.Stop();
        DetailVideo.IsVisible = false;

        if (path == null || !EnsureVlc())
            return;

        // The native VLC surface is positioned when shown and does not track render
        // transforms, so wait for the card's slide-in (220 ms) to settle before showing
        // it; otherwise the video stays offset by the transform at creation time
        var game = _detailGame;
        DispatcherTimer.RunOnce(() =>
        {
            if (!_detailOpen || !ReferenceEquals(_detailGame, game))
                return;

            DetailVideo.IsVisible = true;

            using var media = new Media(_libVlc, path);
            media.AddOption("input-repeat=65535"); // loop the preview
            _mediaPlayer.Play(media);
        }, TimeSpan.FromMilliseconds(320));
    }

    [System.Runtime.InteropServices.DllImport("libc")]
    static extern int setenv(string name, string value, int overwrite);

    bool EnsureVlc()
    {
        if (_mediaPlayer != null)
            return true;

        try
        {
            // On macOS there is no arm64 libvlc nuget: load it from the installed VLC.app.
            // libvlccore finds its plugins through getenv("VLC_PLUGIN_PATH"), and on Unix
            // Environment.SetEnvironmentVariable never reaches the native environment, so
            // the variable has to be set with libc's setenv.
            const string vlcAppLib = "/Applications/VLC.app/Contents/MacOS/lib";
            if (OperatingSystem.IsMacOS() && Directory.Exists(vlcAppLib))
            {
                setenv("VLC_PLUGIN_PATH", "/Applications/VLC.app/Contents/MacOS/plugins", 1);
                Core.Initialize(vlcAppLib);
            }
            else if (OperatingSystem.IsWindows() && FindWindowsVlcPath() is string vlcDir)
            {
                // libvlc isn't bundled with the emulator (it's tens of MB); reuse the
                // user's own VLC install instead, same idea as the macOS branch above.
                Core.Initialize(vlcDir);
            }
            else
            {
                Core.Initialize();
            }
            _libVlc = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVlc);
            DetailVideo.MediaPlayer = _mediaPlayer;
            return true;
        }
        catch (Exception ex)
        {
            ColoredConsole.WriteLine($"[[yellow]]LibVLC not available, game videos disabled: {ex.Message}[[/yellow]]");
            _libVlc?.Dispose();
            _libVlc = null;
            return false;
        }
    }

    /// <summary>
    /// Locates a Windows VLC install containing libvlc.dll: the user-configured directory
    /// (Library configuration window) if set and valid, otherwise the default 64/32-bit
    /// "Program Files" install locations. Returns null when none is found, in which case
    /// Core.Initialize() falls back to its own search (e.g. a bundled libvlc next to the
    /// executable, if one is ever shipped).
    /// </summary>
    static string FindWindowsVlcPath()
    {
        string configured = Config.ConfigOptions.RunninConfig.VlcInstallPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "libvlc.dll")))
            return configured;

        string[] defaultDirs =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VideoLAN", "VLC"),
        };

        foreach (var dir in defaultDirs)
        {
            if (File.Exists(Path.Combine(dir, "libvlc.dll")))
                return dir;
        }

        return null;
    }

    void OnGamesKeyDown(object sender, KeyEventArgs e)
    {
        // The detail overlay is modal: don't move the selection or launch behind it.
        // Its close keys are handled here (not only in OnWindowKeyDown) because the
        // focused ListBoxItem marks Enter/Space handled before they reach the window.
        if (_detailOpen)
        {
            if (e.Key is Key.Escape or Key.Enter or Key.Space)
            {
                CloseDetail();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F5 || (e.Key == Key.R && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
            {
                PickRandomGame();
                e.Handled = true;
                return;
            }
            return;
        }

        if (e.Key == Key.F5 || (e.Key == Key.R && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            PickRandomGame();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                // The game under the mouse wins over the keyboard selection
                LoadGame(_hovered ?? GamesList.SelectedItem as GameEntry);
                e.Handled = true;
                break;

            case Key.Space:
                if ((_hovered ?? GamesList.SelectedItem as GameEntry) is GameEntry game)
                    OpenDetail(game);
                e.Handled = true;
                break;

            case Key.Up:
            case Key.Down:
                MoveSelectionVertical(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                break;

            case Key.PageUp:
                if (CurrentPage > 1)
                {
                    CurrentPage--;
                    UpdatePageDisplay();
                    FocusSelected();
                    e.Handled = true;
                }
                break;

            case Key.PageDown:
                if (CurrentPage < TotalPages)
                {
                    CurrentPage++;
                    UpdatePageDisplay();
                    FocusSelected();
                    e.Handled = true;
                }
                break;

            case Key.Home:
                if (CurrentPage > 1)
                {
                    CurrentPage = 1;
                    UpdatePageDisplay();
                    FocusSelected();
                    e.Handled = true;
                }
                break;

            case Key.End:
                if (CurrentPage < TotalPages)
                {
                    CurrentPage = TotalPages;
                    UpdatePageDisplay();
                    FocusSelected();
                    e.Handled = true;
                }
                break;
        }
    }

    void OnGamesTapped(object sender, TappedEventArgs e)
    {
        // A Ctrl+click already opened the tile's context menu in OnTilePointerPressed
        // (macOS secondary click): it must not double as a launch
        if (OperatingSystem.IsMacOS() && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;

        // Only when the tap landed on a tile, not on empty panel space or the scrollbar
        if (e.Source is Avalonia.Visual visual && visual.FindAncestorOfType<ListBoxItem>(true) != null)
            LoadSelected();
    }

    void OnTilePointerEntered(object sender, PointerEventArgs e) =>
        _hovered = (sender as Control)?.DataContext as GameEntry;

    void OnTilePointerExited(object sender, PointerEventArgs e)
    {
        if (ReferenceEquals(_hovered, (sender as Control)?.DataContext))
            _hovered = null;
    }

    void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        Config.ConfigOptions.RunninConfig.LastLibrarySearch = SearchBox.Text ?? "";
        Program.Config.DumpJsonConfig();
        ApplyFilter();
    }

    void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (_detailOpen)
        {
            if (e.Key is Key.Escape or Key.Enter or Key.Space)
            {
                CloseDetail();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F5 || (e.Key == Key.R && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
            {
                PickRandomGame();
                e.Handled = true;
                return;
            }
            return;
        }

        if (e.Key == Key.F5 || (e.Key == Key.R && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            PickRandomGame();
            e.Handled = true;
            return;
        }

        // Enter launches the hovered game even when the list doesn't have focus
        // (e.g. while typing in the search box); with focus on the list,
        // OnGamesKeyDown already handled it and the event never reaches here
        if (e.Key == Key.Enter && _hovered != null)
        {
            LoadGame(_hovered);
            e.Handled = true;
            return;
        }

        // Space opens the details like in OnGamesKeyDown, but never while typing in
        // the search box, where it must keep inserting spaces
        if (e.Key == Key.Space && !SearchBox.IsFocused)
        {
            if ((_hovered ?? GamesList.SelectedItem as GameEntry) is GameEntry game)
            {
                OpenDetail(game);
                e.Handled = true;
            }
            return;
        }

        if (e.Key != Key.Escape)
            return;

        if (SearchBox.IsFocused && !string.IsNullOrEmpty(SearchBox.Text))
            SearchBox.Text = "";
        else
            Close(null);

        e.Handled = true;
    }

    /// <summary>Per-game view model for the grid: display name, box art and tooltip details.</summary>
    public class GameEntry : INotifyPropertyChanged
    {
        static readonly string[] RegionPreference = { "ss", "eu", "wor", "us" };

        Bitmap _cover;
        string _coverPath;

        public LibraryItem Item { get; }
        public string Name { get; }
        public string CoverPath
        {
            get => _coverPath;
            set
            {
                _coverPath = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverPath)));
            }
        }
        public string Details { get; }
        public string SynopsisText { get; }
        public string VideoPath { get; }
        public string ScreenshotPath { get; }
        public string TitlePath { get; }

        public Bitmap Cover
        {
            get => _cover;
            set
            {
                _cover = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cover)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCover)));
            }
        }

        public bool HasCover => _cover != null;

        public event PropertyChangedEventHandler PropertyChanged;

        public GameEntry(LibraryItem item, MediaIndex mediaIndex)
        {
            Item = item;
            Name = PickText(item.Name) ?? item.Filename ?? item.Id;
            CoverPath = mediaIndex?.FindCover(item.Id);
            Details = BuildDetails(item);
            SynopsisText = PickText(item.Synopsis);
            VideoPath = mediaIndex?.FindVideo(item.Id);
            ScreenshotPath = mediaIndex?.FindScreenshot(item.Id);
            TitlePath = mediaIndex?.FindTitle(item.Id);
        }

        public GameEntry(LibraryItem item, string mediaDir, string libraryPath = null)
        {
            Item = item;
            Name = PickText(item.Name) ?? item.Filename ?? item.Id;
            CoverPath = FindCover(item, mediaDir, libraryPath);
            Details = BuildDetails(item);
            SynopsisText = PickText(item.Synopsis);
            // The scraper saves the video-normalized preview as <Id>.<format>
            VideoPath = FindMedia(item, mediaDir, "{0}.*", libraryPath);
            ScreenshotPath = FindMedia(item, mediaDir, "Screenshot-{0}.*", libraryPath)
                          ?? FindMedia(item, mediaDir, "TitleScreen-{0}.*", libraryPath);
            TitlePath = FindMedia(item, mediaDir, "TitleScreen-{0}.*", libraryPath);
        }

        /// <summary>First media file matching the pattern ({0} = game id), or null.</summary>
        static string FindMedia(LibraryItem item, string mediaDir, string pattern, string libraryPath = null)
        {
            if (string.IsNullOrEmpty(item.Id))
                return null;

            var mediaDirs = new List<string>();
            if (!string.IsNullOrEmpty(mediaDir) && Directory.Exists(mediaDir))
                mediaDirs.Add(mediaDir);

            if (!string.IsNullOrEmpty(libraryPath))
            {
                if (!string.IsNullOrEmpty(item.Filename))
                {
                    string sub = Path.GetDirectoryName(item.Filename);
                    if (!string.IsNullOrEmpty(sub))
                    {
                        string subMedia = Path.IsPathRooted(sub) ? Path.Combine(sub, "Media") : Path.Combine(libraryPath, sub, "Media");
                        if (Directory.Exists(subMedia) && !mediaDirs.Contains(subMedia, StringComparer.OrdinalIgnoreCase))
                            mediaDirs.Add(subMedia);
                    }
                }

                string defaultStxMedia = Path.Combine(Config.GetDefaultTosecPath(), "Atari ST - Games - [STX] (TOSEC-v2011-03-20_CM)", "Media");
                if (Directory.Exists(defaultStxMedia) && !mediaDirs.Contains(defaultStxMedia, StringComparer.OrdinalIgnoreCase))
                    mediaDirs.Add(defaultStxMedia);
            }

            foreach (var dir in mediaDirs)
            {
                try
                {
                    string match = Directory.EnumerateFiles(dir, string.Format(pattern, item.Id)).FirstOrDefault();
                    if (match != null)
                        return match;

                    string baseId = Regex.Replace(item.Id, @"_cr_.*|_t_.*|_one_disk.*|\[.*\]", "", RegexOptions.IgnoreCase).TrimEnd('_');
                    if (!string.IsNullOrEmpty(baseId) && baseId.Length >= 5 && baseId != item.Id)
                    {
                        string prefixPattern = string.Format(pattern, baseId + "*");
                        string prefixMatch = Directory.EnumerateFiles(dir, prefixPattern).FirstOrDefault();
                        if (prefixMatch != null)
                            return prefixMatch;
                    }
                }
                catch { }
            }

            return null;
        }

        static string PickText(List<LibraryItem.RegionText> texts)
        {
            if (texts == null)
                return null;

            foreach (var region in RegionPreference)
            {
                var match = texts.Find(t => t.Region == region && !string.IsNullOrWhiteSpace(t.Text));
                if (match != null)
                    return Decode(match.Text);
            }

            return Decode(texts.Find(t => !string.IsNullOrWhiteSpace(t.Text))?.Text);
        }

        // ScreenScraper texts come HTML-encoded (&quot;, &amp;…)
        static string Decode(string text) =>
            text == null ? null : System.Net.WebUtility.HtmlDecode(text);

        static string FindCover(LibraryItem item, string mediaDir, string libraryPath = null)
        {
            if (string.IsNullOrEmpty(item.Id))
                return null;

            var mediaDirs = new List<string>();
            if (!string.IsNullOrEmpty(mediaDir) && Directory.Exists(mediaDir))
                mediaDirs.Add(mediaDir);

            if (!string.IsNullOrEmpty(libraryPath))
            {
                if (!string.IsNullOrEmpty(item.Filename))
                {
                    string sub = Path.GetDirectoryName(item.Filename);
                    if (!string.IsNullOrEmpty(sub))
                    {
                        string subMedia = Path.IsPathRooted(sub) ? Path.Combine(sub, "Media") : Path.Combine(libraryPath, sub, "Media");
                        if (Directory.Exists(subMedia) && !mediaDirs.Contains(subMedia, StringComparer.OrdinalIgnoreCase))
                            mediaDirs.Add(subMedia);
                    }
                }

                string defaultStxMedia = Path.Combine(Config.GetDefaultTosecPath(), "Atari ST - Games - [STX] (TOSEC-v2011-03-20_CM)", "Media");
                if (Directory.Exists(defaultStxMedia) && !mediaDirs.Contains(defaultStxMedia, StringComparer.OrdinalIgnoreCase))
                    mediaDirs.Add(defaultStxMedia);
            }

            foreach (var dir in mediaDirs)
            {
                try
                {
                    // 1. Exact match
                    string png = Path.Combine(dir, $"Box-{item.Id}.png");
                    if (File.Exists(png))
                        return png;

                    string anyExt = Directory.EnumerateFiles(dir, $"Box-{item.Id}.*").FirstOrDefault();
                    if (anyExt != null)
                        return anyExt;

                    // 2. Clean title match
                    string clean = ArtworkScraperService.CleanGameTitle(item.Id);
                    if (!string.IsNullOrEmpty(clean))
                    {
                        string cleanPng = Path.Combine(dir, $"Box-{clean}.png");
                        if (File.Exists(cleanPng)) return cleanPng;
                        string underPng = Path.Combine(dir, $"Box-{clean.Replace(' ', '_')}.png");
                        if (File.Exists(underPng)) return underPng;
                    }

                    // 3. Prefix / normalized match (strip cracker/dump tags)
                    string baseId = Regex.Replace(item.Id, @"_cr_.*|_t_.*|_one_disk.*|\[.*\]", "", RegexOptions.IgnoreCase).TrimEnd('_');
                    if (!string.IsNullOrEmpty(baseId) && baseId.Length >= 5 && baseId != item.Id)
                    {
                        string prefixMatch = Directory.EnumerateFiles(dir, $"Box-{baseId}*.*").FirstOrDefault();
                        if (prefixMatch != null)
                            return prefixMatch;
                    }
                }
                catch { }
            }

            return null;
        }

        static string BuildDetails(LibraryItem item)
        {
            var lines = new List<string>();

            if (!string.IsNullOrWhiteSpace(item.Developer)) lines.Add($"Developer: {item.Developer}");
            if (!string.IsNullOrWhiteSpace(item.Publisher)) lines.Add($"Publisher: {item.Publisher}");

            string date = PickText(item.Date);
            if (date != null) lines.Add($"Released: {date}");

            string genre = PickText(item.Genre);
            if (genre != null) lines.Add($"Genre: {genre}");

            if (!string.IsNullOrWhiteSpace(item.Filename)) lines.Add($"File: {item.Filename}");

            return lines.Count > 0 ? string.Join("\n", lines) : null;
        }
    }

    public class LibraryCacheData
    {
        public long DirectoryTicks { get; set; }
        public List<LibraryItem> Items { get; set; } = new();
    }

    public class MediaIndex
    {
        private readonly Dictionary<string, string> _boxExact = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _boxClean = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _boxNorm = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _titleMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _titleClean = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _screenshotMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _screenshotClean = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _videoMap = new(StringComparer.OrdinalIgnoreCase);

        public static MediaIndex Build(IEnumerable<string> directories)
        {
            var index = new MediaIndex();
            var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in directories)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir) || !seenDirs.Add(dir))
                    continue;

                try
                {
                    var files = Directory.EnumerateFiles(dir, "*.*");
                    foreach (var file in files)
                    {
                        string fname = Path.GetFileName(file);
                        string ext = Path.GetExtension(fname).ToLowerInvariant();

                        if (fname.StartsWith("Box-", StringComparison.OrdinalIgnoreCase))
                        {
                            string id = Path.GetFileNameWithoutExtension(fname)[4..];
                            index._boxExact.TryAdd(id, file);
                            string clean = CleanId(id);
                            if (!string.IsNullOrEmpty(clean))
                            {
                                index._boxClean.TryAdd(clean, file);
                                string norm = NormalizeKey(clean);
                                if (!string.IsNullOrEmpty(norm))
                                    index._boxNorm.TryAdd(norm, file);
                            }
                        }
                        else if (fname.StartsWith("TitleScreen-", StringComparison.OrdinalIgnoreCase))
                        {
                            string id = Path.GetFileNameWithoutExtension(fname)[12..];
                            index._titleMap.TryAdd(id, file);
                            string clean = CleanId(id);
                            if (!string.IsNullOrEmpty(clean))
                                index._titleClean.TryAdd(clean, file);
                        }
                        else if (fname.StartsWith("Screenshot-", StringComparison.OrdinalIgnoreCase))
                        {
                            string id = Path.GetFileNameWithoutExtension(fname)[11..];
                            index._screenshotMap.TryAdd(id, file);
                            string clean = CleanId(id);
                            if (!string.IsNullOrEmpty(clean))
                                index._screenshotClean.TryAdd(clean, file);
                        }
                        else if (ext is ".mp4" or ".mkv" or ".avi" or ".webm")
                        {
                            string id = Path.GetFileNameWithoutExtension(fname);
                            index._videoMap.TryAdd(id, file);
                            string clean = CleanId(id);
                            if (!string.IsNullOrEmpty(clean))
                                index._videoMap.TryAdd(clean, file);
                        }
                        else if (ext is ".png" or ".jpg" or ".jpeg")
                        {
                            string id = Path.GetFileNameWithoutExtension(fname);
                            index._boxExact.TryAdd(id, file);
                            string clean = CleanId(id);
                            if (!string.IsNullOrEmpty(clean))
                            {
                                index._boxClean.TryAdd(clean, file);
                                string norm = NormalizeKey(clean);
                                if (!string.IsNullOrEmpty(norm))
                                    index._boxNorm.TryAdd(norm, file);
                            }
                        }
                    }
                }
                catch { }
            }

            return index;
        }

        public static string CleanId(string raw)
        {
            return ArtworkScraperService.CleanGameTitle(raw);
        }

        public static string NormalizeKey(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            return Regex.Replace(CleanId(raw), @"[^a-zA-Z0-9]", "").ToLowerInvariant();
        }

        public string FindCover(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (_boxExact.TryGetValue(id, out var path)) return path;
            string clean = CleanId(id);
            if (!string.IsNullOrEmpty(clean) && _boxClean.TryGetValue(clean, out path)) return path;
            string norm = NormalizeKey(id);
            if (!string.IsNullOrEmpty(norm) && _boxNorm.TryGetValue(norm, out path)) return path;

            // Fallback: title screen or screenshot if available
            if (FindTitle(id) is { } title) return title;
            if (FindScreenshot(id) is { } snap) return snap;

            return null;
        }

        public string FindTitle(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (_titleMap.TryGetValue(id, out var path)) return path;
            string clean = CleanId(id);
            if (!string.IsNullOrEmpty(clean) && _titleClean.TryGetValue(clean, out path)) return path;
            return null;
        }

        public string FindScreenshot(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (_screenshotMap.TryGetValue(id, out var path)) return path;
            string clean = CleanId(id);
            if (!string.IsNullOrEmpty(clean) && _screenshotClean.TryGetValue(clean, out path)) return path;
            return _titleMap.TryGetValue(id, out path) ? path : null;
        }

        public string FindVideo(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (_videoMap.TryGetValue(id, out var path)) return path;
            string clean = CleanId(id);
            if (!string.IsNullOrEmpty(clean) && _videoMap.TryGetValue(clean, out path)) return path;
            return null;
        }
    }
}
