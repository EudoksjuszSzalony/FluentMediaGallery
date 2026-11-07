using System.Collections.ObjectModel;
using FluentMediaGallery.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;

namespace FluentMediaGallery.App;

public sealed partial class MainWindow : Window
{
    private const int PageSize = 100;

    private readonly LibraryDatabase _database = new();
    private readonly DirectoryIndexer _indexer;
    private readonly RatingImportService _ratingImporter;
    private readonly FavoritesService _favorites;
    private readonly ObservableCollection<SourceRow> _sources = new();
    private readonly ObservableCollection<FolderNodeRow> _folderRows = new();
    private readonly HashSet<string> _expandedFolders = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedFolderPath;
    private readonly ObservableCollection<MediaRow> _rows = new();
    private readonly ShellThumbnailCache _thumbnailCache = new();
    private readonly List<ViewerWindow> _openViewers = new();
    private readonly DispatcherQueue _uiDispatcher;

    private CancellationTokenSource? _activeOperation;
    private MediaEntry? _selectedMedia;
    private long? _selectedSourceId;
    private int _currentPage;
    private long _matchedCount;
    private int _queryVersion;
    private bool _isReady;
    private bool _metadataWriteConfirmed;

    public MainWindow()
    {
        InitializeComponent();

        _indexer = new DirectoryIndexer(_database);
        _ratingImporter = new RatingImportService(_database, new ShellRatingReader());
        _favorites = new FavoritesService(_database, new ShellRatingReader(), new ShellRatingWriter());
        _uiDispatcher = DispatcherQueue.GetForCurrentThread();

        SourceItems.ItemsSource = _folderRows;
        MediaItems.ItemsSource = _rows;
        ViewerFilmstrip.ItemsSource = _rows;
        LayoutRoot.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Viewer_KeyDown), true);
        Closed += (_, _) => _activeOperation?.Cancel();

        // Startup uses only persisted SQLite. Never enumerate folders implicitly.
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _database.InitializeAsync();
            _isReady = true;
            await ReloadSourcesAsync();
            await LoadPageAsync();
            OperationStatus.Text = "Gotowe. Dodaj folder i uruchom indeksowanie, gdy zechcesz.";
        }
        catch (Exception ex)
        {
            OperationStatus.Text = "Nie udało się otworzyć bazy: " + ex.Message;
            _isReady = false;
        }
    }

    private async Task ReloadSourcesAsync()
    {
        var sources = await _database.ListSourcesAsync();
        _sources.Clear();
        foreach (var source in sources) _sources.Add(new SourceRow(source));
        if (_selectedSourceId.HasValue && sources.All(s => s.Id != _selectedSourceId))
        {
            _selectedSourceId = null;
            _selectedFolderPath = null;
        }

        await ReloadFolderTreeAsync();
        UpdateHeader();
    }

    private async Task ReloadFolderTreeAsync()
    {
        var rows = new List<FolderNodeRow>();
        foreach (var root in _sources)
            await AppendFolderAsync(rows, root.Source.Id, root.Source.Root, 0,
                isRoot: true, root.Status);

        var shortcuts = await _database.ListShortcutsAsync();
        foreach (var shortcut in shortcuts)
            rows.Add(new FolderNodeRow(shortcut.SourceId, shortcut.FolderPath, 0,
                false, true, false, "Skrót · ten sam indeks"));

        _folderRows.Clear();
        foreach (var row in rows) _folderRows.Add(row);
    }

    private async Task AppendFolderAsync(List<FolderNodeRow> rows, long sourceId,
        string folder, int depth, bool isRoot, string status)
    {
        var expanded = _expandedFolders.Contains(folder);
        rows.Add(new FolderNodeRow(sourceId, folder, depth, isRoot, false, expanded,
            status));
        if (!expanded || depth >= 20) return;

        var children = await _database.GetIndexedChildFoldersAsync(sourceId, folder);
        foreach (var child in children)
            await AppendFolderAsync(rows, sourceId, child, depth + 1, false, "");
    }

    private void UpdateHeader()
    {
        if (_selectedFolderPath is not null)
        {
            HeaderTitle.Text = Path.GetFileName(_selectedFolderPath.TrimEnd('\\'));
            return;
        }
        var selected = _sources.FirstOrDefault(s => s.Source.Id == _selectedSourceId);
        HeaderTitle.Text = selected?.Title ?? "Wszystkie media";
    }

    private async Task LoadPageAsync()
    {
        if (!_isReady) return;
        var version = ++_queryVersion;
        try
        {
            var search = SearchBox.Text.Trim();
            var favoritesOnly = FavoritesOnly.IsChecked == true;
            var source = _selectedSourceId;
            var folder = _selectedFolderPath;

            var count = await _database.CountFilteredAsync(source, favoritesOnly, search, folderPath: folder);
            var maxPage = count == 0 ? 0 : (int)((count - 1) / PageSize);
            if (_currentPage > maxPage) _currentPage = maxPage;
            var page = await _database.GetPageAsync(
                _currentPage * PageSize, PageSize, favoritesOnly, search, sourceId: source, folderPath: folder);
            if (version != _queryVersion) return;

            _matchedCount = count;
            _rows.Clear();
            foreach (var entry in page) _rows.Add(new MediaRow(entry));

            var first = count == 0 ? 0 : _currentPage * PageSize + 1;
            var last = Math.Min(count, (_currentPage + 1L) * PageSize);
            PageLabel.Text = $"{first:N0}–{last:N0} z {count:N0}";
            LibrarySummary.Text = $"{count:N0} elementów · rekordy z SQLite (100 na stronę)";
            FirstPageButton.IsEnabled = _currentPage > 0;
            PreviousButton.IsEnabled = _currentPage > 0;
            NextButton.IsEnabled = (_currentPage + 1L) * PageSize < count;
        }
        catch (Exception ex)
        {
            OperationStatus.Text = "Błąd odczytu galerii: " + ex.Message;
        }
    }

    private async Task RegisterLocationAsync(string path)
    {
        var registered = await _database.RegisterLocationAsync(path);
        _selectedSourceId = registered.SourceId;
        _selectedFolderPath = registered.IsShortcut ? registered.FolderPath : null;
        _currentPage = 0;
        SourcePath.Text = "";
        await ReloadSourcesAsync();
        await LoadPageAsync();
        OperationStatus.Text = registered.IsShortcut
            ? "Dodano skrót podfolderu bez ponownego indeksowania. Rozwiń źródło po lewej stronie."
            : registered.IsNew
                ? "Folder dodany. Uruchom indeksowanie wybranego źródła."
                : "Ten folder jest już w bibliotece.";
    }

    private async void AddSource_Click(object sender, RoutedEventArgs e)
    {
        if (!_isReady || _activeOperation is not null) return;
        try
        {
            await RegisterLocationAsync(SourcePath.Text.Trim());
        }
        catch (Exception ex)
        {
            OperationStatus.Text = "Błąd dodawania lokalizacji: " + ex.Message;
        }
    }

    private async void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        if (!_isReady || _activeOperation is not null) return;
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            await RegisterLocationAsync(folder.Path);
        }
        catch (Exception ex)
        {
            OperationStatus.Text = "Nie udało się wybrać folderu: " + ex.Message;
        }
    }

    private async void FolderToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FolderNodeRow folder } || folder.IsShortcut)
            return;
        if (!_expandedFolders.Add(folder.Path))
            _expandedFolders.Remove(folder.Path);
        await ReloadFolderTreeAsync();
    }

    private async void AllSources_Click(object sender, RoutedEventArgs e)
    {
        _selectedSourceId = null;
        _selectedFolderPath = null;
        _currentPage = 0;
        UpdateHeader();
        await LoadPageAsync();
    }

    private async void SourceItems_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FolderNodeRow row) return;
        _selectedSourceId = row.SourceId;
        _selectedFolderPath = row.IsRoot ? null : row.Path;
        _currentPage = 0;
        UpdateHeader();
        await LoadPageAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!_isReady) return;
        await ReloadSourcesAsync();
        await LoadPageAsync();
        OperationStatus.Text = "Widok odświeżony z indeksu SQLite; foldery nie były skanowane.";
    }

    private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        _currentPage = 0;
        await LoadPageAsync();
    }

    private async void FavoritesOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isReady) return;
        _currentPage = 0;
        await LoadPageAsync();
    }

    private async void FirstPage_Click(object sender, RoutedEventArgs e)
    {
        _currentPage = 0;
        await LoadPageAsync();
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        _currentPage = Math.Max(0, _currentPage - 1);
        await LoadPageAsync();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if ((_currentPage + 1L) * PageSize >= _matchedCount) return;
        _currentPage++;
        await LoadPageAsync();
    }

    private void MediaItems_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not MediaRow row) return;
        row.EnsureThumbnail(_thumbnailCache, _uiDispatcher);
    }

    private async void MediaItems_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not MediaRow row) return;
        _selectedMedia = row.Entry;
        DetailsName.Text = row.Entry.Name;
        DetailsPath.Text = row.Entry.FullPath;
        DetailsRating.Text = row.Entry.FileRating is int rating
            ? $"System.Rating: {rating} / 99"
            : "System.Rating: nieodczytany / niedostępny";
        FavoriteButton.Content = row.Entry.IsFavorite ? "Usuń z ulubionych" : "Dodaj do ulubionych";
        FavoriteButton.IsEnabled = true;
        var state = await _database.GetFavoriteWriteStateAsync(row.Entry.Id);
        FavoriteStatus.Text = state.Pending
            ? "Zapis do pliku oczekuje: " + (state.Error ?? "niezsynchronizowany")
            : "";

        // Viewer captures filter, folder and sort context at the moment of opening.
        // It owns an independent Window and can navigate without mutating the gallery UI.
        int index = _rows.ToList().FindIndex(r => r.Entry.Id == row.Entry.Id);
        if (index < 0) return;
        var context = new GalleryViewContext(
            _selectedSourceId, _selectedFolderPath,
            FavoritesOnly.IsChecked == true, SearchBox.Text.Trim());
        var viewer = new ViewerWindow(_database, _favorites, _thumbnailCache,
            context, () => _ = LoadPageAsync());
        _openViewers.Add(viewer);
        viewer.Closed += (_, _) => _openViewers.Remove(viewer);
        viewer.Activate();
        await viewer.OpenAtAsync(_currentPage * (long)PageSize + index);
    }

    private async void Favorite_Click(object sender, RoutedEventArgs e)
        => await ToggleFavoriteAsync();

    private async Task ToggleFavoriteAsync()
    {
        if (_selectedMedia is not MediaEntry media || _activeOperation is not null) return;
        if (!_metadataWriteConfirmed)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = LayoutRoot.XamlRoot,
                Title = "Zapis klasyfikacji w pliku",
                Content = "Ta operacja zapisze System.Rating w oryginalnym pliku, jeżeli format na to pozwala. " +
                          "Pierwsze testy wykonuj wyłącznie na KOPIACH mediów. Kontynuować?",
                PrimaryButtonText = "Zapisz klasyfikację",
                CloseButtonText = "Anuluj",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            _metadataWriteConfirmed = true;
        }

        FavoriteButton.IsEnabled = false;
        ViewerFavoriteButton.IsEnabled = false;
        try
        {
            var desired = !media.IsFavorite;
            var result = await Task.Run(() => _favorites.SetFavoriteAsync(media.Id, desired));
            _selectedMedia = await _database.GetByIdAsync(media.Id);
            FavoriteButton.Content = desired ? "Usuń z ulubionych" : "Dodaj do ulubionych";
            FavoriteStatus.Text = result.StoredInFile
                ? "Zapisano i zweryfikowano ocenę w pliku."
                : "Ulubione zapisano tylko w SQLite. Plik: " + result.Error;
            await LoadPageAsync();

            if (_selectedMedia is not null)
                UpdateViewerAfterFavorite(_selectedMedia, FavoriteStatus.Text);
        }
        catch (Exception ex)
        {
            FavoriteStatus.Text = "Błąd zmiany ulubionych: " + ex.Message;
            if (_viewerVisible) ViewerStatus.Text = FavoriteStatus.Text;
        }
        finally
        {
            FavoriteButton.IsEnabled = _selectedMedia is not null;
            ViewerFavoriteButton.IsEnabled = _selectedMedia is not null;
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (!_isReady || _activeOperation is not null) return;
        var source = _sources.FirstOrDefault(s => s.Source.Id == _selectedSourceId)?.Source;
        if (source is null)
        {
            OperationStatus.Text = "Najpierw wybierz jeden z folderów po lewej stronie.";
            return;
        }

        using var cts = new CancellationTokenSource();
        SetOperation(cts, true);
        OperationStatus.Text = "Trwa indeksowanie: " + source.Root;
        try
        {
            var progress = new Progress<int>(n =>
                _uiDispatcher.TryEnqueue(() => OperationStatus.Text = $"Indeksowanie: {n:N0} plików · {source.Root}"));
            var result = await Task.Run(() => _indexer.ScanAsync(source, progress, cts.Token), cts.Token);
            OperationStatus.Text = result.Completed
                ? $"Zindeksowano {result.Discovered:N0} plików. Trwa odczyt klasyfikacji..."
                : $"Niepełny skan / niedostępne źródło. Zachowano indeks. Błędy: {result.Errors}.";
            await ReloadSourcesAsync();
            await LoadPageAsync();

            // A complete scan is followed by read-only System.Rating import for THIS source.
            // It runs in the background, in batches, and can be canceled at any time.
            if (result.Completed)
            {
                var imported = await Task.Run(async () =>
                {
                    int count = 0;
                    while (true)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        int step = await _ratingImporter.ImportNextBatchAsync(64, cts.Token, source.Id);
                        if (step == 0) break;
                        count += step;
                        int snapshot = count;
                        _uiDispatcher.TryEnqueue(() =>
                            OperationStatus.Text = $"Wczytywanie klasyfikacji: {snapshot:N0} · {source.Root}");
                    }
                    return count;
                }, cts.Token);

                OperationStatus.Text = $"Zindeksowano {result.Discovered:N0} plików i odczytano klasyfikację {imported:N0} plików.";
                await LoadPageAsync();
            }
        }
        catch (OperationCanceledException)
        {
            OperationStatus.Text = "Anulowano indeksowanie lub odczyt klasyfikacji. Zapisane wpisy pozostają w SQLite.";
            await LoadPageAsync();
        }
        catch (Exception ex)
        {
            OperationStatus.Text = "Błąd indeksowania (bez czyszczenia katalogu): " + ex.Message;
        }
        finally
        {
            SetOperation(cts, false);
        }
    }

    private async void ImportRatings_Click(object sender, RoutedEventArgs e)
    {
        if (!_isReady || _activeOperation is not null) return;
        using var cts = new CancellationTokenSource();
        SetOperation(cts, true);
        OperationStatus.Text = "Odczyt klasyfikacji z plików — bez zapisywania metadanych.";
        try
        {
            var total = await Task.Run(async () =>
            {
                var counter = 0;
                while (true)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var step = await _ratingImporter.ImportNextBatchAsync(64, cts.Token);
                    if (step == 0) break;
                    counter += step;
                    var count = counter;
                    _uiDispatcher.TryEnqueue(() =>
                        OperationStatus.Text = $"Odczytano klasyfikacje {count:N0} plików...");
                }
                return counter;
            }, cts.Token);
            OperationStatus.Text = $"Odczyt klasyfikacji zakończony ({total:N0} plików).";
            await LoadPageAsync();
        }
        catch (OperationCanceledException)
        {
            OperationStatus.Text = "Odczyt ocen anulowany. Zapisany postęp pozostał w SQLite.";
            await LoadPageAsync();
        }
        catch (Exception ex)
        {
            OperationStatus.Text = "Nie udało się odczytać ocen: " + ex.Message;
        }
        finally
        {
            SetOperation(cts, false);
        }
    }

    private void SetOperation(CancellationTokenSource source, bool active)
    {
        _activeOperation = active ? source : null;
        ScanButton.IsEnabled = !active;
        RatingButton.IsEnabled = !active;
        AddSourceButton.IsEnabled = !active;
        BrowseSourceButton.IsEnabled = !active;
        CancelButton.IsEnabled = active;
        ScanProgress.IsIndeterminate = active;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _activeOperation?.Cancel();
        OperationStatus.Text = "Trwa anulowanie bieżącej operacji...";
    }
}
