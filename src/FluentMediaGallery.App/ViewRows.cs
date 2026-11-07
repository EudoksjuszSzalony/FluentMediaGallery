using System.ComponentModel;
using FluentMediaGallery.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

namespace FluentMediaGallery.App;

public sealed class SourceRow
{
    public LibrarySource Source { get; }
    public string Title { get; }
    public string Status { get; }

    public SourceRow(LibrarySource source)
    {
        Source = source;
        Title = Path.GetFileName(source.Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(Title)) Title = source.Root;
        Status = source.LastError is not null
            ? "Błąd / offline — dane zachowane"
            : source.LastScanUtc is null ? "Nieindeksowany" : "Indeksowany";
    }
}

public sealed class MediaRow : INotifyPropertyChanged
{
    private bool _loadingStarted;
    private BitmapImage? _thumbnail;

    public MediaEntry Entry { get; }
    public string Name => Entry.Name;
    public string Description => Entry.Kind == MediaKind.Video ? "Film" : "Zdjęcie";
    public string TypeMark => Entry.Kind == MediaKind.Video ? "▶" : "▧";
    public string Heart => Entry.IsFavorite ? "♥" : "";
    public string SizeText => Entry.Size >= 1024 * 1024
        ? $"{Entry.Size / 1048576d:0.0} MB"
        : $"{Entry.Size / 1024d:0} KB";

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MediaRow(MediaEntry entry) => Entry = entry;

    public async void EnsureThumbnail(ShellThumbnailCache cache, DispatcherQueue dispatcher)
    {
        if (_loadingStarted) return;
        _loadingStarted = true;
        try
        {
            var path = await cache.GetOrCreateAsync(Entry);
            if (path is null) return;

            // BitmapImage is a XAML object: create and update it only on the UI thread.
            dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(path);
                    using var stream = await file.OpenReadAsync();
                    var image = new BitmapImage();
                    await image.SetSourceAsync(stream);
                    Thumbnail = image;
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException or ArgumentException
                       or System.Runtime.InteropServices.COMException)
                {
                    // Keep the placeholder when a thumbnail is unavailable.
                }
            });
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException
               or System.Runtime.InteropServices.COMException)
        {
            // Source may be temporarily offline.
        }
    }
}
