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
    private double _imageWidth;
    private double _imageHeight;

    public ViewerWindow(LibraryDatabase database, FavoritesService favorites,
        ShellThumbnailCache thumbnailCache, GalleryViewContext context, Action onFavoriteChanged)
    {
        InitializeComponent();
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
        if (_total > 0) await ShowCurrentAsync();
        else ImageMessage.Text = "Brak plików w wybranym widoku.";
        PreviousButton.Focus(FocusState.Programmatic);
    }

    private async Task ShowCurrentAsync()
    {
        var ticket = ++_loadVersion;
        if (_total <= 0) return;

        var pageStart = (int)((_offset / PageSize) * PageSize);
        var pageItems = await _database.GetPageAsync(pageStart, PageSize, _context.FavoritesOnly,
            _context.Search, sourceId: _context.SourceId, folderPath: _context.FolderPath);
        if (ticket != _loadVersion || !_ready) return;

        _currentPage.Clear();
        foreach (var item in pageItems) _currentPage.Add(new MediaRow(item));
        int localIndex = (int)(_offset - pageStart);
        if (localIndex < 0 || localIndex >= _currentPage.Count) return;

        _suppressFilmstrip = true;
        try
        {
            Filmstrip.SelectedIndex = localIndex;
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
        Status.Text = media.FullPath;
        ImageMessage.Text = "";
        FullImage.Source = null;
        LoadingIndicator.IsActive = true;
        ResetZoom();

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
        double w = Math.Max(320, ZoomScroll.ActualWidth - 16);
        double h = Math.Max(240, ZoomScroll.ActualHeight - 16);
        double scale = Math.Min(1.0, Math.Min(w / _imageWidth, h / _imageHeight));
        FullImage.Width = Math.Max(1, Math.Round(_imageWidth * scale));
        FullImage.Height = Math.Max(1, Math.Round(_imageHeight * scale));
    }

    private void ResetZoom()
    {
        ChangeZoom(100);
    }

    private void ChangeZoom(double percentage)
    {
        var value = Math.Clamp(percentage, 25, 500);
        _updatingZoom = true;
        try
        {
            ZoomSlider.Value = value;
            ZoomLabel.Text = $"{value:0}%";
            ZoomScroll.ChangeView(null, null, (float)(value / 100d), true);
        }
        finally { _updatingZoom = false; }
    }

    private void ZoomSlider_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_ready || _updatingZoom) return;
        ChangeZoom(e.NewValue);
    }

    private void ZoomScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!_ready || _updatingZoom) return;
        var percent = ZoomScroll.ZoomFactor * 100;
        _updatingZoom = true;
        ZoomSlider.Value = Math.Clamp(percent, 25, 500);
        ZoomLabel.Text = $"{percent:0}%";
        _updatingZoom = false;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ChangeZoom(ZoomSlider.Value + 10);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ChangeZoom(ZoomSlider.Value - 10);
    private void ZoomFit_Click(object sender, RoutedEventArgs e) => ChangeZoom(100);

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
        FilmstripRow.Height = new GridLength(_filmstripVisible ? 112 : 0);
        FilmstripButton.Content = _filmstripVisible ? "Ukryj pasek (F)" : "Pokaż pasek (F)";
    }

    private async void Filmstrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _suppressFilmstrip || _navigating || Filmstrip.SelectedIndex < 0) return;
        long pageStart = (_offset / PageSize) * PageSize;
        long requested = pageStart + Filmstrip.SelectedIndex;
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
                    ChangeZoom(ZoomSlider.Value + 10);
                    e.Handled = true;
                }
                break;
            case VirtualKey.Subtract:
                if (ControlPressed())
                {
                    ChangeZoom(ZoomSlider.Value - 10);
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

    private static void AnimateOpacity(UIElement target, double value)
    {
        var animation = new DoubleAnimation
        {
            To = value,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private void PreviousPointerEntered(object sender, PointerRoutedEventArgs e)
        => AnimateOpacity(PreviousButton, 0.92);
    private void PreviousPointerExited(object sender, PointerRoutedEventArgs e)
        => AnimateOpacity(PreviousButton, 0);
    private void NextPointerEntered(object sender, PointerRoutedEventArgs e)
        => AnimateOpacity(NextButton, 0.92);
    private void NextPointerExited(object sender, PointerRoutedEventArgs e)
        => AnimateOpacity(NextButton, 0);

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
