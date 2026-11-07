namespace FluentMediaGallery.Core;

public enum MediaKind { Photo = 1, Video = 2 }

public sealed record LibrarySource(long Id, string Root, bool IsOnline, string? LastScanUtc, string? LastError);

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
    bool IsFavorite);

public sealed record MediaFile(
    string FullPath, string Name, MediaKind Kind, long Size, long LastWriteUtcTicks);

public sealed record RatingCandidate(long Id, string Path);

public sealed record ScanResult(long SourceId, int Discovered, int Errors, bool Completed, bool Online);

public static class FavoriteRatingPolicy
{
    // Microsoft Photos' historical favorites convention, not a general 4-or-more star threshold.
    public static bool IsLegacyFavorite(int? rawRating) => rawRating is >= 63 and <= 87;
    public static bool IsLegacyUnfavorite(int? rawRating) => rawRating is >= 1 and <= 12;
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
