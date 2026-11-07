using System.Collections.ObjectModel;
using FluentMediaGallery.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FluentMediaGallery.App;

public sealed partial class MainWindow : Window
{
    private const int PageSize = 100;

    private readonly LibraryDatabase _database = new();
    private readonly DirectoryIndexer _indexer;
    private readonly RatingImportService _ratingImporter;
    private readonly FavoritesService _favorites;
    private readonly ObservableCollection<SourceRow> _sources = new();
    private readonly ObservableCollection<MediaRow> _rows = new();
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

        SourceItems.ItemsSource = _sources;
        MediaItems.ItemsSource = _rows;
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
            _selectedSourceId = null;
        UpdateHeader();
    }

    private void UpdateHeader()
    {
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

            var count = await _database.CountFilteredAsync(source, favoritesOnly, search);
            var maxPage = count == 0 ? 0 : (int)((count - 1) / PageSize);
            if (_currentPage > maxPage) _currentPage = maxPage;
            var page = await _database.GetPageAsync(
                _currentPage * PageSize, PageSize, favoritesOnly, search, sourceId: source);
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

    private async void AddSource_Click(object sender, RoutedEventArgs e)
    {
        if (!_isReady || _activeOperation is not null) return;
        try
        {
            var input = SourcePath.Text.Trim();
            if (string.IsNullOrWhiteSpace(input) || !Path.IsPathFullyQualified(input))
            {
                OperationStatus.Text = "Podaj pełną ścieżkę, np. D:\\Zdjęcia lub \\\\serwer\\udział\\zdjęcia.";
                return;
            }

            var source = await _database.AddSourceAsync(input);
            _selectedSourceId = source.Id;
            _currentPage = 0;
            SourcePath.Text = "";
            await ReloadSourcesAsync();
            await LoadPageAsync();
            OperationStatus.Text = "Folder zarejestrowany. Kliknij „Indeksuj wybrany folder”, aby odczytać pliki.";
        }
        catch (Exception ex)
        {
            OperationStatus.Text = "Błąd dodawania folderu: " + ex.Message;
        }
    }

    private async void AllSources_Click(object sender, RoutedEventArgs e)
    {
        _selectedSourceId = null;
        _currentPage = 0;
        UpdateHeader();
        await LoadPageAsync();
    }

    private async void SourceItems_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SourceRow row) return;
        _selectedSourceId = row.Source.Id;
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
    }

    private async void Favorite_Click(object sender, RoutedEventArgs e)
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
        }
        catch (Exception ex)
        {
            FavoriteStatus.Text = "Błąd zmiany ulubionych: " + ex.Message;
        }
        finally
        {
            FavoriteButton.IsEnabled = _selectedMedia is not null;
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
                ? $"Zindeksowano {result.Discovered:N0} plików. Bez błędów."
                : $"Niepełny skan / niedostępne źródło. Zachowano indeks. Błędy: {result.Errors}.";
            await ReloadSourcesAsync();
            await LoadPageAsync();
        }
        catch (OperationCanceledException)
        {
            OperationStatus.Text = "Indeksowanie anulowane. Wcześniejsze wpisy zachowane.";
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
        CancelButton.IsEnabled = active;
        ScanProgress.IsIndeterminate = active;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _activeOperation?.Cancel();
        OperationStatus.Text = "Trwa anulowanie bieżącej operacji...";
    }
}
