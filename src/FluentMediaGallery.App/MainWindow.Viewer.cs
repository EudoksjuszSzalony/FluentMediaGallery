using FluentMediaGallery.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

namespace FluentMediaGallery.App;

public sealed partial class MainWindow
{
    private bool _viewerVisible;
    private bool _viewerSelectingFilmstrip;
    private bool _viewerNavigating;
    private int _viewerIndex;
    private int _viewerLoadSerial;

    private async Task OpenViewerAsync(MediaEntry item)
    {
        var index = _rows.ToList().FindIndex(r => r.Entry.Id == item.Id);
        if (index < 0) return;

        _viewerVisible = true;
        ViewerOverlay.Visibility = Visibility.Visible;
        await ShowViewerIndexAsync(index);
        ViewerCloseButton.Focus(FocusState.Programmatic);
    }

    private async Task ShowViewerIndexAsync(int index)
    {
        if (!_viewerVisible || index < 0 || index >= _rows.Count) return;

        _viewerIndex = index;
        var entry = _rows[index].Entry;
        _selectedMedia = entry;
        var ticket = ++_viewerLoadSerial;

        ViewerFileName.Text = entry.Name;
        ViewerPosition.Text = $"{(_currentPage * PageSize + index + 1):N0} z {_matchedCount:N0} · " +
                              (entry.Kind == MediaKind.Photo ? "Zdjęcie" : "Film");
        ViewerFavoriteButton.Content = entry.IsFavorite
            ? "♥ W ulubionych"
            : "♡ Dodaj do ulubionych";
        ViewerPreviousButton.IsEnabled = _currentPage * PageSize + index > 0;
        ViewerNextButton.IsEnabled = _currentPage * PageSize + index + 1 < _matchedCount;

        _viewerSelectingFilmstrip = true;
        try
        {
            ViewerFilmstrip.SelectedIndex = index;
            ViewerFilmstrip.ScrollIntoView(_rows[index]);
        }
        finally { _viewerSelectingFilmstrip = false; }

        ViewerImage.Source = null;
        ViewerStatus.Text = string.Empty;
        ViewerLoading.IsActive = entry.Kind == MediaKind.Photo;

        if (entry.Kind == MediaKind.Video)
        {
            ViewerLoading.IsActive = true;
            ViewerStatus.Text = "Film — odtwarzacz wideo zostanie dodany w następnym etapie.";
            try
            {
                var thumbnailPath = await _thumbnailCache.GetOrCreateAsync(entry);
                if (thumbnailPath is not null)
                {
                    var thumbnailFile = await StorageFile.GetFileFromPathAsync(thumbnailPath);
                    using var stream = await thumbnailFile.OpenReadAsync();
                    var thumbnail = new BitmapImage();
                    await thumbnail.SetSourceAsync(stream);
                    if (ticket == _viewerLoadSerial && _viewerVisible)
                        ViewerImage.Source = thumbnail;
                }
            }
            catch (Exception ex)
            {
                if (ticket == _viewerLoadSerial && _viewerVisible)
                    ViewerStatus.Text = "Brak podglądu filmu: " + ex.Message;
            }
            finally
            {
                if (ticket == _viewerLoadSerial)
                    ViewerLoading.IsActive = false;
            }
            return;
        }

        try
        {
            // Loading starts only when opened. Bitmap decoder limits decoded width, not original file.
            var sourceFile = await StorageFile.GetFileFromPathAsync(entry.FullPath);
            using var stream = await sourceFile.OpenReadAsync();
            var bitmap = new BitmapImage { DecodePixelWidth = 2560 };
            await bitmap.SetSourceAsync(stream);

            if (ticket == _viewerLoadSerial && _viewerVisible)
                ViewerImage.Source = bitmap;
        }
        catch (Exception ex)
        {
            if (ticket == _viewerLoadSerial && _viewerVisible)
                ViewerStatus.Text = "Nie można otworzyć zdjęcia: " + ex.Message;
        }
        finally
        {
            if (ticket == _viewerLoadSerial)
                ViewerLoading.IsActive = false;
        }
    }

    private async Task NavigateViewerAsync(int delta)
    {
        if (!_viewerVisible || _viewerNavigating || delta is not (-1 or 1)) return;
        _viewerNavigating = true;
        try
        {
            long absoluteIndex = _currentPage * (long)PageSize + _viewerIndex + delta;
            if (absoluteIndex < 0 || absoluteIndex >= _matchedCount) return;

            var targetPage = (int)(absoluteIndex / PageSize);
            if (targetPage != _currentPage)
            {
                _currentPage = targetPage;
                await LoadPageAsync();
            }

            var index = (int)(absoluteIndex % PageSize);
            if (index < _rows.Count)
                await ShowViewerIndexAsync(index);
        }
        finally { _viewerNavigating = false; }
    }

    private void CloseViewer()
    {
        _viewerVisible = false;
        ++_viewerLoadSerial;
        ViewerImage.Source = null;
        ViewerLoading.IsActive = false;
        ViewerOverlay.Visibility = Visibility.Collapsed;
    }

    private void ViewerClose_Click(object sender, RoutedEventArgs e) => CloseViewer();

    private async void ViewerPrevious_Click(object sender, RoutedEventArgs e)
        => await NavigateViewerAsync(-1);

    private async void ViewerNext_Click(object sender, RoutedEventArgs e)
        => await NavigateViewerAsync(1);

    private async void ViewerFilmstrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_viewerVisible || _viewerSelectingFilmstrip ||
            ViewerFilmstrip.SelectedIndex < 0 ||
            ViewerFilmstrip.SelectedIndex == _viewerIndex) return;

        await ShowViewerIndexAsync(ViewerFilmstrip.SelectedIndex);
    }

    private void ViewerFilmstrip_ContainerContentChanging(
        ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not MediaRow row) return;
        row.EnsureThumbnail(_thumbnailCache, _uiDispatcher);
    }

    private async void ViewerFavorite_Click(object sender, RoutedEventArgs e)
        => await ToggleFavoriteAsync();

    private void Viewer_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_viewerVisible) return;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                CloseViewer();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Left:
                _ = NavigateViewerAsync(-1);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Right:
                _ = NavigateViewerAsync(1);
                e.Handled = true;
                break;
        }
    }

    private void UpdateViewerAfterFavorite(MediaEntry media, string status)
    {
        if (!_viewerVisible) return;

        int newIndex = _rows.ToList().FindIndex(r => r.Entry.Id == media.Id);
        if (newIndex < 0)
        {
            // A removed favorite disappears from a favorites-only filtered page.
            CloseViewer();
            return;
        }

        _viewerIndex = newIndex;
        ViewerFavoriteButton.Content = media.IsFavorite
            ? "♥ W ulubionych" : "♡ Dodaj do ulubionych";
        ViewerStatus.Text = status;
        _viewerSelectingFilmstrip = true;
        try { ViewerFilmstrip.SelectedIndex = newIndex; }
        finally { _viewerSelectingFilmstrip = false; }
    }
}
