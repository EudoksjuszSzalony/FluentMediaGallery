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
    private readonly CancellationTokenSource _thumbnailCancellation = new();

    // Called when this media row leaves the bounded visible-query window.
    // Cancels queued Shell thumbnail work and releases our decoded bitmap.
    public void ReleaseThumbnail()
    {
        if (!_thumbnailCancellation.IsCancellationRequested)
            _thumbnailCancellation.Cancel();
        Thumbnail = null;
    }

    private double _selectionOpacity;
    public double SelectionOpacity => _selectionOpacity;

    public void SetCurrent(bool value)
    {
        var next = value ? 1d : 0d;
        if (_selectionOpacity == next) return;
        _selectionOpacity = next;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionOpacity)));
    }

    public MediaEntry Entry { get; private set; }

    public void UpdateEntry(MediaEntry updated)
    {
        Entry = updated;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Heart)));
    }
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
            var ct = _thumbnailCancellation.Token;
            var path = await cache.GetOrCreateAsync(Entry, ct);
            if (path is null || ct.IsCancellationRequested) return;

            // BitmapImage is a XAML object: create and update it only on the UI thread.
            dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    if (ct.IsCancellationRequested) return;
                    var file = await StorageFile.GetFileFromPathAsync(path);
                    using var stream = await file.OpenReadAsync();
                    var image = new BitmapImage();
                    await image.SetSourceAsync(stream);
                    if (!ct.IsCancellationRequested)
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
        catch (OperationCanceledException)
        {
            // Media left the gallery window while queued for thumbnail extraction.
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException
               or System.Runtime.InteropServices.COMException)
        {
            // Source may be temporarily offline.
        }
    }
}

public sealed class FolderNodeRow
{
    public long SourceId { get; }
    public string Path { get; }
    public bool IsShortcut { get; }
    public bool IsRoot { get; }
    public int Depth { get; }
    public string DisplayTitle { get; }
    public string Status { get; }
    public string Chevron { get; }
    public Microsoft.UI.Xaml.Thickness Indent { get; }
    public Microsoft.UI.Xaml.Visibility StatusVisibility =>
        string.IsNullOrEmpty(Status)
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;

    public FolderNodeRow(long sourceId, string path, int depth, bool isRoot,
        bool isShortcut, bool expanded, string status)
    {
        SourceId = sourceId;
        Path = path;
        IsRoot = isRoot;
        IsShortcut = isShortcut;
        Depth = depth;
        DisplayTitle = (isShortcut ? "↗ " : "") +
            (System.IO.Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : path);
        Status = status;
        Chevron = isShortcut ? " " : expanded ? "▾" : "▸";
        Indent = new Microsoft.UI.Xaml.Thickness(Math.Min(12 * depth, 72), 0, 0, 0);
    }
}
