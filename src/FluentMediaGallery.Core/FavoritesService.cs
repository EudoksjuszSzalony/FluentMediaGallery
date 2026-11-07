namespace FluentMediaGallery.Core;

/// <summary>
/// File-first durability goal with safe fallback to SQLite for read-only/unsupported formats.
/// Writes are serialized so rapidly toggling a heart cannot interleave writes.
/// </summary>
public sealed class FavoritesService(
    LibraryDatabase database,
    ShellRatingReader reader,
    ShellRatingWriter writer)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task<FavoriteUpdateResult> SetFavoriteAsync(long mediaId, bool favorite,
        CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            if (await database.GetByIdAsync(mediaId, ct) is null)
                throw new KeyNotFoundException("Unknown media id: " + mediaId);
            // Persist choice before trying a potentially slow network filesystem write.
            await database.SetFavoriteOverrideAsync(mediaId, favorite, ct);
            return await SynchronizePendingAsync(mediaId, ct);
        }
        finally { _writeGate.Release(); }
    }

    public async Task<int> RetryPendingWritesAsync(int batchSize = 32, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            var items = await database.GetPendingFavoriteWritesAsync(batchSize, ct);
            var successful = 0;
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var result = await SynchronizePendingAsync(item.Id, ct);
                if (result.StoredInFile) successful++;
            }
            return successful;
        }
        finally { _writeGate.Release(); }
    }

    private async Task<FavoriteUpdateResult> SynchronizePendingAsync(long mediaId, CancellationToken ct)
    {
        var media = await database.GetByIdAsync(mediaId, ct)
            ?? throw new KeyNotFoundException("Unknown media id: " + mediaId);
        if (media.FavoriteOverride is not bool favorite)
            return new(mediaId, media.IsFavorite, false, "No local favorite override.");

        // Keep an existing 5-star file at 5 stars when user marks it as favorite.
        var currentFileRating = favorite ? reader.TryRead(media.FullPath) : null;
        int desired = !favorite ? 1 : currentFileRating is >= 88 and <= 99 ? 99 : 75;
        var write = writer.TryWrite(media.FullPath, desired);
        if (write.Success)
        {
            await database.CompleteFavoriteFileWriteAsync(mediaId, favorite, desired, ct);
            return new(mediaId, favorite, true, null);
        }

        var error = write.Error ?? "Could not persist System.Rating to source file.";
        await database.RecordFavoriteFileWriteErrorAsync(mediaId, favorite, error, ct);
        return new(mediaId, favorite, false, error);
    }
}
