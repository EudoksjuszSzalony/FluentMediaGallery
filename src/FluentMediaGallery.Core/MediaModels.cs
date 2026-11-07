namespace FluentMediaGallery.Core;

public enum MediaKind { Photo = 1, Video = 2 }

public sealed record LibrarySource(long Id, string Root, bool IsOnline, string? LastScanUtc, string? LastError);
public sealed record MediaLocation(long SourceId, string FolderPath, bool IsShortcut, bool IsNew);

public sealed record MediaEntry(
    long Id,
    long SourceId,
    string FullPath,
    string Name,
    MediaKind Kind,
    long Size,
    long LastWriteUtcTicks,
    int? FileRating,
    bool? FavoriteOverride,
    bool IsFavorite,
    long CreatedUtcTicks = 0,
    int PixelWidth = 0,
    int PixelHeight = 0);

public sealed record MediaFile(
    string FullPath, string Name, MediaKind Kind, long Size, long LastWriteUtcTicks,
    long CreatedUtcTicks = 0);

/// <summary>Dimensions captured from a source header; only persisted for matching indexed file version.</summary>
public sealed record MediaDimension(long Id, long ModifiedUtcTicks, long Size, int Width, int Height);

public sealed record RatingCandidate(long Id, string Path);
public sealed record FavoriteWriteCandidate(long Id, string Path, bool Favorite);
public sealed record RatingWriteResult(bool Success, int? PersistedRating, string? Error);
public sealed record FavoriteUpdateResult(long MediaId, bool IsFavorite, bool StoredInFile, string? Error);

public sealed record ScanResult(long SourceId, int Discovered, int Errors, bool Completed, bool Online);

public static class FavoriteRatingPolicy
{
    // Microsoft Photos' historical favorites convention, not a general 4-or-more star threshold.
    public static bool IsLegacyFavorite(int? rawRating) => rawRating is >= 63 and <= 99;
    public static bool IsLegacyUnfavorite(int? rawRating) => rawRating is >= 1 and <= 12;
    public static int ToStars(int rawRating) => rawRating switch
    {
        <= 0 => 0,
        <= 12 => 1,
        <= 37 => 2,
        <= 62 => 3,
        <= 87 => 4,
        _ => 5
    };
    public static bool IsFavorite(bool? appOverride, int? rawRating)
        => appOverride ?? IsLegacyFavorite(rawRating);
}

public static class MediaFormats
{
    private static readonly HashSet<string> Photos = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif", ".dng" };
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".mpg", ".mpeg", ".ts", ".mts", ".m2ts", ".3gp" };

    public static MediaKind? Identify(string path)
    {
        var extension = Path.GetExtension(path);
        if (Photos.Contains(extension)) return MediaKind.Photo;
        if (Videos.Contains(extension)) return MediaKind.Video;
        return null;
    }
}
