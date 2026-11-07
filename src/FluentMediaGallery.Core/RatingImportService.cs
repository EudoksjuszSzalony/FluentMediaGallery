namespace FluentMediaGallery.Core;

/// <summary>
/// Run separately from file discovery (low-priority background worker).
/// A batch is bounded so massive libraries do not block application startup.
/// </summary>
public sealed class RatingImportService(LibraryDatabase database, ShellRatingReader reader)
{
    public async Task<int> ImportNextBatchAsync(int maximum = 64, CancellationToken ct = default)
    {
        var candidates = await database.GetPendingRatingsAsync(maximum, ct);
        var imported = 0;
        foreach (var item in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(item.Path)) continue; // SMB dropped: retry after reconnection
            var rating = reader.TryRead(item.Path);
            await database.SaveFileRatingAsync(item.Id, rating, ct);
            imported++;
        }
        return imported;
    }
}
