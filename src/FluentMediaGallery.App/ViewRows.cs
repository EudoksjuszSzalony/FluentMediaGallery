using FluentMediaGallery.Core;

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

public sealed class MediaRow
{
    public MediaEntry Entry { get; }
    public string Name => Entry.Name;
    public string Description => Entry.Kind == MediaKind.Video ? "Film" : "Zdjęcie";
    public string TypeMark => Entry.Kind == MediaKind.Video ? "▶" : "▧";
    public string Heart => Entry.IsFavorite ? "♥" : "";
    public string SizeText => Entry.Size >= 1024 * 1024
        ? $"{Entry.Size / 1048576d:0.0} MB"
        : $"{Entry.Size / 1024d:0} KB";

    public MediaRow(MediaEntry entry) => Entry = entry;
}
