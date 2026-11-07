using System.Collections.ObjectModel;
using System.Diagnostics;
using FluentMediaGallery.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using VirtualKey = Windows.System.VirtualKey;

namespace FluentMediaGallery.App;

public sealed record GalleryViewContext(
    long? SourceId, string? FolderPath, bool FavoritesOnly,
    string Search, string SortKey = "modified-desc");

/// <summary>
/// Standalone WinUI viewer window. Captures source/search/favorites/sort context
/// at launch so gallery changes cannot silently change viewer navigation.
/// Every page comes from SQLite; never materializes the full library.
/// </summary>
public sealed partial class ViewerWindow : Window
{
    private const int PageSize = 100;
    private readonly LibraryDatabase _database;
    private readonly FavoritesService _favorites;
    private readonly ShellThumbnailCache _thumbnailCache;
    private readonly GalleryViewContext _context;
    private readonly Action _onFavoriteChanged;
    private readonly ObservableCollection<MediaRow> _currentPage = new();
    private readonly DispatcherQueue _dispatcher;
    private long _total;
    private long _offset;
    private long _selectedMediaId;
    private int _loadVersion;
    private bool _filmstripVisible = true;
    private bool _suppressFilmstrip;
    private bool _updatingZoom;
    private bool _navigating;
    private bool _writeConfirmed;
    private bool _ready;
    private bool _detailsVisible;
    private double _imageWidth;
    private double _imageHeight;
    private double _fitScale = 1;
    // The image's layout size is 50% of decoded pixels. This permits 5–500%
    // real zoom while keeping WinUI ScrollViewer.zoomFactor within [0.1, 10].
    private const double ImageBaseScale = 0.5;
    private double _currentPercent = 100;
    private bool _fitToWindow = true;
    private long _loadedPageStart = -1;
    private int _selectedLocalIndex = -1;
    private Storyboard? _previousFade;
    private Storyboard? _nextFade;

    public ViewerWindow(LibraryDatabase database, FavoritesService favorites,
        ShellThumbnailCache thumbnailCache, GalleryViewContext context, Action onFavoriteChanged)
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleDragRegion);
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(255, 65, 68, 74);
        AppWindow.TitleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
        _database = database;
        _favorites = favorites;
        _thumbnailCache = thumbnailCache;
        _context = context;
        _onFavoriteChanged = onFavoriteChanged;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Filmstrip.ItemsSource = _currentPage;

        ViewerRoot.AddHandler(UIElement.KeyDownEvent,
            new KeyEventHandler(Viewer_KeyDown), true);
        PhotoSurface.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(Viewer_MouseWheel), true);

        this.AppWindow.Resize(new Windows.Graphics.SizeInt32 { Width = 1440, Height = 920 });
        this.Closed += (_, _) => { ++_loadVersion; _ready = false; };
    }

    public async Task OpenAtAsync(long offset)
    {
        _ready = true;
        _total = await _database.CountFilteredAsync(_context.SourceId, _context.FavoritesOnly,
            _context.Search, folderPath: _context.FolderPath);
        _offset = Math.Clamp(offset, 0, Math.Max(0, _total - 1));
        ZoomLabel.Text = "—%";
        if (_total > 0) await ShowCurrentAsync();
        else ImageMessage.Text = "Brak plików w wybranym widoku.";
        PreviousButton.Focus(FocusState.Programmatic);
    }

    private async Task ShowCurrentAsync()
    {
        var ticket = ++_loadVersion;
        if (_total <= 0) return;

        var pageStart = (int)((_offset / PageSize) * PageSize);
        // Keep the same MediaRow/BitmapImage objects while moving inside one page.
        // Rebuilding the collection on every photo was causing the filmstrip to blink.
        if (_loadedPageStart != pageStart)
        {
            var pageItems = await _database.GetPageAsync(pageStart, PageSize, _context.FavoritesOnly,
                _context.Search, sourceId: _context.SourceId, folderPath: _context.FolderPath,
                sortKey: _context.SortKey);
            if (ticket != _loadVersion || !_ready) return;
            _currentPage.Clear();
            foreach (var item in pageItems) _currentPage.Add(new MediaRow(item));
            _loadedPageStart = pageStart;
            _selectedLocalIndex = -1;
        }
        int localIndex = (int)(_offset - pageStart);
        if (localIndex < 0 || localIndex >= _currentPage.Count) return;

        _suppressFilmstrip = true;
        try
        {
            if (_selectedLocalIndex >= 0 && _selectedLocalIndex < _currentPage.Count)
                _currentPage[_selectedLocalIndex].SetCurrent(false);
            _selectedLocalIndex = localIndex;
            _currentPage[localIndex].SetCurrent(true);
            Filmstrip.ScrollIntoView(_currentPage[localIndex]);
        }
        finally { _suppressFilmstrip = false; }

        var media = _currentPage[localIndex].Entry;
        _selectedMediaId = media.Id;
        FileTitle.Text = media.Name;
        ContextTitle.Text = $"{_offset + 1:N0} / {_total:N0} · {GetContextLabel()} · {_context.SortKey}";
        FavoriteButton.Content = media.IsFavorite ? "♥ W ulubionych (L)" : "♡ Dodaj do ulubionych (L)";
        FavoriteButton.IsEnabled = true;
        PreviousButton.IsEnabled = _offset > 0;
        NextButton.IsEnabled = _offset + 1 < _total;
        DetailsNameText.Text = media.Name;
        DetailsTypeText.Text = media.Kind == MediaKind.Video ? "Film" : "Zdjęcie";
        DetailsSizeText.Text = $"{media.Size / 1024d / 1024d:0.##} MB";
        DetailsRatingText.Text = media.FileRating.HasValue
            ? $"Klasyfikacja: {FavoriteRatingPolicy.ToStars(media.FileRating.Value)} ★ ({media.FileRating}/99)"
            : "Klasyfikacja: brak / nieodczytana";
        DetailsPathText.Text = media.FullPath;
        Status.Text = media.FullPath;
        ImageMessage.Text = "";
        FullImage.Source = null;
        LoadingIndicator.IsActive = true;
        _imageWidth = 0;
        _imageHeight = 0;
        _fitToWindow = true;
        ZoomLabel.Text = "—%";

        try
        {
            if (media.Kind == MediaKind.Video)
            {
                var thumbPath = await _thumbnailCache.GetOrCreateAsync(media);
                if (ticket != _loadVersion || !_ready) return;
                if (thumbPath is null)
                {
                    ImageMessage.Text = "Film — brak miniaturki. Odtwarzacz zostanie dodany później.";
                }
                else
                {
                    var file = await StorageFile.GetFileFromPathAsync(thumbPath);
                    using var stream = await file.OpenReadAsync();
                    var image = new BitmapImage();
                    await image.SetSourceAsync(stream);
                    if (ticket != _loadVersion || !_ready) return;
                    FullImage.Source = image;
                    SetImageFitDimensions(image);
                    ImageMessage.Text = "Film — podgląd klatki; odtwarzacz wideo jest jeszcze w przygotowaniu.";
                }
                return;
            }

            var sourceFile = await StorageFile.GetFileFromPathAsync(media.FullPath);
            using var imageStream = await sourceFile.OpenReadAsync();
            var bitmap = new BitmapImage { DecodePixelWidth = 3840 };
            await bitmap.SetSourceAsync(imageStream);
            if (ticket != _loadVersion || !_ready) return;
            FullImage.Source = bitmap;
            SetImageFitDimensions(bitmap);
        }
        catch (Exception ex)
        {
            if (ticket == _loadVersion && _ready)
                ImageMessage.Text = "Nie można otworzyć: " + ex.Message;
        }
        finally
        {
            if (ticket == _loadVersion)
                LoadingIndicator.IsActive = false;
        }
    }

    private string GetContextLabel()
    {
        if (_context.FavoritesOnly) return "Ulubione";
        if (_context.FolderPath is not null) return _context.FolderPath;
        return _context.SourceId is null ? "Cała galeria" : "Wybrane źródło";
    }

    private void SetImageFitDimensions(BitmapImage bitmap)
    {
        _imageWidth = bitmap.PixelWidth;
        _imageHeight = bitmap.PixelHeight;
        if (_imageWidth <= 0 || _imageHeight <= 0) return;
        RecalculateFit();
        if (_fitToWindow) FitToWindow();
        else ChangeZoom(_currentPercent, animate: false);
        // Retry after the image's layout pass: ChangeView can be rejected when
        // the ScrollViewer has not measured its new content yet.
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!_ready || !ReferenceEquals(FullImage.Source, bitmap) || !_fitToWindow)
                return;
            RecalculateFit();
            FitToWindow();
        });
    }

    private void PhotoSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_ready || _imageWidth <= 0 || _imageHeight <= 0) return;
        RecalculateFit();
        if (_fitToWindow) FitToWindow();
        else ChangeZoom(_currentPercent, animate: false);
    }

    private void RecalculateFit()
    {
        if (_imageWidth <= 0 || _imageHeight <= 0) return;
        // If a new window hasn't completed its first layout yet, fall back to
        // the real AppWindow dimensions instead of fitting to a 1-pixel viewport.
        // PhotoSurface.SizeChanged will re-fit once WinUI reports the final size.
        var w = ZoomScroll.ActualWidth > 100
            ? ZoomScroll.ActualWidth - 12 : Math.Max(320, AppWindow.Size.Width - 130);
        var h = ZoomScroll.ActualHeight > 100
            ? ZoomScroll.ActualHeight - 12 : Math.Max(240, AppWindow.Size.Height - 235);
        _fitScale = Math.Min(w / _imageWidth, h / _imageHeight);
        _fitScale = Math.Clamp(_fitScale, 0.001, 10);
        FullImage.Width = Math.Max(1, _imageWidth * ImageBaseScale);
        FullImage.Height = Math.Max(1, _imageHeight * ImageBaseScale);
    }

    private void FitToWindow()
    {
        if (_imageWidth <= 0) return;
        _fitToWindow = true;
        ChangeZoom(100 * _fitScale, animate: true, fitting: true);
    }

    private void ChangeZoom(double percent, bool animate = true, bool fitting = false)
    {
        if (_imageWidth <= 0 || _imageHeight <= 0) return;
        if (!fitting) _fitToWindow = false;
        // Public percentage is relative to decoded image pixels. Internal
        // ScrollViewer factor is relative to the half-size layout image.
        var target = Math.Clamp(percent / (100 * ImageBaseScale), 0.1, 10);
        _currentPercent = target * ImageBaseScale * 100;
        _updatingZoom = true;
        try
        {
            ZoomScroll.ChangeView(null, null, (float)target, disableAnimation: !animate);
            var display = Math.Clamp(_currentPercent, ZoomSlider.Minimum, ZoomSlider.Maximum);
            ZoomSlider.Value = display;
            ZoomLabel.Text = $"{_currentPercent:0}%";
            ZoomModeButton.Content = _fitToWindow ? "Dopasuj ✓" : "Dopasuj";
        }
        finally { _updatingZoom = false; }
    }

    private void ResetZoom() => FitToWindow();

    private void ZoomSlider_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_ready || _updatingZoom || _imageWidth <= 0) return;
        ChangeZoom(e.NewValue);
    }

    private void ZoomScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!_ready || _updatingZoom || _imageWidth <= 0) return;
        _currentPercent = ZoomScroll.ZoomFactor * ImageBaseScale * 100;
        _updatingZoom = true;
        try
        {
            ZoomSlider.Value = Math.Clamp(_currentPercent, ZoomSlider.Minimum, ZoomSlider.Maximum);
            ZoomLabel.Text = $"{_currentPercent:0}%";
            ZoomModeButton.Content = _fitToWindow ? "Dopasuj ✓" : "Dopasuj";
        }
        finally { _updatingZoom = false; }
    }

    private void ZoomModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_fitToWindow) ChangeZoom(100);
        else FitToWindow();
    }

    private void ZoomFit_Click(object sender, RoutedEventArgs e) => FitToWindow();

    // Fit is an actual stop between 25..500%, not a hardcoded 100% label.
    private void ZoomStep(int direction)
    {
        if (_imageWidth <= 0) return;
        var fit = Math.Round(_fitScale * 100);
        var stops = new[] { 5d, 10, 15, 25, 33, 50, 67, 75, 100, 125, 150, 200, 250, 300, 400, 500, fit }
            .Where(p => p >= 5 && p <= 500).Distinct().OrderBy(p => p).ToArray();
        var next = direction > 0
            ? stops.FirstOrDefault(p => p > _currentPercent + 0.5, stops[^1])
            : stops.LastOrDefault(p => p < _currentPercent - 0.5, stops[0]);
        if (Math.Abs(next - fit) < 0.5) FitToWindow();
        else ChangeZoom(next);
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomStep(1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomStep(-1);

    private async Task NavigateAsync(int direction)
    {
        if (!_ready || _navigating || direction is not (-1 or 1)) return;
        var next = _offset + direction;
        if (next < 0 || next >= _total) return;
        _navigating = true;
        try
        {
            _offset = next;
            await ShowCurrentAsync();
        }
        finally { _navigating = false; }
    }

    private async void Previous_Click(object sender, RoutedEventArgs e) => await NavigateAsync(-1);
    private async void Next_Click(object sender, RoutedEventArgs e) => await NavigateAsync(1);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void FilmstripToggle_Click(object sender, RoutedEventArgs e) => ToggleFilmstrip();

    private void ToggleFilmstrip()
    {
        _filmstripVisible = !_filmstripVisible;
        FilmstripBorder.Visibility = _filmstripVisible ? Visibility.Visible : Visibility.Collapsed;
        FilmstripRow.Height = new GridLength(_filmstripVisible ? 90 : 0);
        FilmstripButton.Content = _filmstripVisible ? "Pasek ✓ (F)" : "Pasek (F)";
    }

    private void DetailsToggle_Click(object sender, RoutedEventArgs e)
    {
        _detailsVisible = !_detailsVisible;
        DetailsPane.Visibility = _detailsVisible ? Visibility.Visible : Visibility.Collapsed;
        DetailsButton.Content = _detailsVisible ? "ⓘ Szczegóły ✓" : "ⓘ Szczegóły";
    }

    private async void Filmstrip_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (!_ready || _suppressFilmstrip || _navigating ||
            e.ClickedItem is not MediaRow clicked) return;
        var index = _currentPage.IndexOf(clicked);
        if (index < 0) return;
        var requested = (_offset / PageSize) * PageSize + index;
        if (requested == _offset || requested >= _total) return;
        _offset = requested;
        await ShowCurrentAsync();
    }

    private void Filmstrip_ContainerContentChanging(
        ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        if (!e.InRecycleQueue && e.Item is MediaRow row)
            row.EnsureThumbnail(_thumbnailCache, _dispatcher);
    }

    private async void Favorite_Click(object sender, RoutedEventArgs e) => await ToggleFavoriteAsync();

    private async Task ToggleFavoriteAsync()
    {
        if (!_ready || _selectedMediaId <= 0) return;

        if (!_writeConfirmed)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = ViewerRoot.XamlRoot,
                Title = "Zmiana metadanych pliku",
                Content = "Zmiana ulubionych spróbuje zapisać System.Rating w samym pliku. " +
                          "Testuj najpierw na kopiach zdjęć i filmów. Kontynuować?",
                PrimaryButtonText = "Zmień ulubione",
                CloseButtonText = "Anuluj",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            _writeConfirmed = true;
        }

        var entry = await _database.GetByIdAsync(_selectedMediaId);
        if (entry is null) return;
        FavoriteButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => _favorites.SetFavoriteAsync(entry.Id, !entry.IsFavorite));
            Status.Text = result.StoredInFile ? "System.Rating zapisany i zweryfikowany."
                : "Ulubione zapisano w SQLite, zapis do pliku nie powiódł się: " + result.Error;
            _onFavoriteChanged();
            var changed = await _database.GetByIdAsync(entry.Id);
            if (changed is not null)
            {
                var row = _currentPage.FirstOrDefault(x => x.Entry.Id == changed.Id);
                row?.UpdateEntry(changed);
            }

            // Removing a favorite from favorites-only or favorite ordering changes
            // the sequence; only then do we refresh the current filmstrip page.
            if (_context.FavoritesOnly || _context.SortKey.StartsWith("favorites", StringComparison.Ordinal))
                _loadedPageStart = -1;

            _total = await _database.CountFilteredAsync(_context.SourceId, _context.FavoritesOnly,
                _context.Search, folderPath: _context.FolderPath);
            if (_total == 0)
            {
                _selectedMediaId = 0;
                Close();
                return;
            }

            _offset = Math.Min(_offset, _total - 1);
            await ShowCurrentAsync();
        }
        catch (Exception ex)
        {
            Status.Text = "Błąd ulubionych: " + ex.Message;
        }
        finally { FavoriteButton.IsEnabled = _ready; }
    }

    private void Viewer_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_ready) return;
        switch (e.Key)
        {
            case VirtualKey.Escape:
                Close();
                e.Handled = true;
                break;
            case VirtualKey.F:
                ToggleFilmstrip();
                e.Handled = true;
                break;
            case VirtualKey.L:
                _ = ToggleFavoriteAsync();
                e.Handled = true;
                break;
            case VirtualKey.Left:
                _ = NavigateAsync(-1);
                e.Handled = true;
                break;
            case VirtualKey.Right:
                _ = NavigateAsync(1);
                e.Handled = true;
                break;
            case VirtualKey.Add:
                if (ControlPressed())
                {
                    ZoomStep(1);
                    e.Handled = true;
                }
                break;
            case VirtualKey.Subtract:
                if (ControlPressed())
                {
                    ZoomStep(-1);
                    e.Handled = true;
                }
                break;
            case VirtualKey.NumberPad0:
                if (ControlPressed())
                {
                    ResetZoom();
                    e.Handled = true;
                }
                break;
        }
    }

    private static bool ControlPressed()
    {
        var state = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control);
        return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
    }

    private void Viewer_MouseWheel(object sender, PointerRoutedEventArgs e)
    {
        if (!_ready) return;
        var delta = e.GetCurrentPoint(PhotoSurface).Properties.MouseWheelDelta;
        if (delta != 0)
        {
            _ = NavigateAsync(delta > 0 ? -1 : 1);
            e.Handled = true;
        }
    }

    private static void FadeButton(Button target, ref Storyboard? running, double opacity)
    {
        // Stop prior animation; otherwise hover-out can replay stale flashes.
        running?.Stop();
        var animation = new DoubleAnimation
        {
            To = opacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(150))
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        running = storyboard;
        storyboard.Begin();
    }

    private void PreviousPointerEntered(object sender, PointerRoutedEventArgs e)
        => FadeButton(PreviousButton, ref _previousFade, 1);
    private void PreviousPointerExited(object sender, PointerRoutedEventArgs e)
        => FadeButton(PreviousButton, ref _previousFade, 0);
    private void NextPointerEntered(object sender, PointerRoutedEventArgs e)
        => FadeButton(NextButton, ref _nextFade, 1);
    private void NextPointerExited(object sender, PointerRoutedEventArgs e)
        => FadeButton(NextButton, ref _nextFade, 0);

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMediaId <= 0) return;
        _ = CopyPathAsync();
    }

    private async Task CopyPathAsync()
    {
        var entry = await _database.GetByIdAsync(_selectedMediaId);
        if (entry is null) return;
        var package = new DataPackage();
        package.SetText(entry.FullPath);
        Clipboard.SetContent(package);
        Status.Text = "Skopiowano ścieżkę pliku.";
    }

    private async void Explorer_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMediaId <= 0) return;
        try
        {
            var entry = await _database.GetByIdAsync(_selectedMediaId);
            if (entry is null) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + entry.FullPath.Replace("\"", "") + "\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Status.Text = "Nie można otworzyć Eksploratora: " + ex.Message;
        }
    }
}
