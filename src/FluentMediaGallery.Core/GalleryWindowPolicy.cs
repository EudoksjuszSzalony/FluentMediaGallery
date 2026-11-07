namespace FluentMediaGallery.Core;

/// <summary>
/// Memory budget for the gallery's two-way SQLite paging. Only loaded media rows
/// are retained; off-screen thumbnails are discarded when their batch leaves the
/// window. A large library can still be navigated by seeking on the timeline.
/// </summary>
public static class GalleryWindowPolicy
{
    public const int BatchSize = 100;
    public const int MaxRetainedMedia = 400;

    /// <summary>Number of earliest/latest rows to evict in complete batches.</summary>
    public static int TrimExcess(int retained, int incoming)
    {
        if (retained < 0 || incoming < 0)
            throw new ArgumentOutOfRangeException(nameof(retained));
        var excess = (long)retained + incoming - MaxRetainedMedia;
        if (excess <= 0) return 0;
        var chunks = (excess + BatchSize - 1) / BatchSize;
        return checked((int)(chunks * BatchSize));
    }

    public static int PreviousBatchOffset(int currentStart)
        => Math.Max(0, currentStart - BatchSize);
}
