namespace FluentMediaGallery.Core;

public sealed class DirectoryIndexer(LibraryDatabase database)
{
    // One source is scanned at a time by its caller. UI must call this on a background task.
    public async Task<ScanResult> ScanAsync(LibrarySource source, IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(source.Root))
        {
            await database.SetSourceStateAsync(source.Id, false, "Source unavailable; index preserved.", false, ct);
            return new ScanResult(source.Id, 0, 0, false, false);
        }

        var generation = DateTime.UtcNow.Ticks;
        var queue = new Stack<string>();
        queue.Push(source.Root);
        var batch = new List<MediaFile>(512);
        var scanned = 0;
        var errors = 0;
        await database.SetSourceStateAsync(source.Id, true, null, false, ct);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = queue.Pop();
            try
            {
                foreach (var item in Directory.EnumerateFileSystemEntries(directory))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var attrs = File.GetAttributes(item);
                        if ((attrs & FileAttributes.ReparsePoint) != 0) continue; // avoid symlink loops
                        if ((attrs & FileAttributes.Directory) != 0)
                        {
                            queue.Push(item);
                            continue;
                        }
                        var kind = MediaFormats.Identify(item);
                        if (!kind.HasValue) continue;
                        var info = new FileInfo(item);
                        batch.Add(new MediaFile(item, info.Name, kind.Value, info.Length, info.LastWriteTimeUtc.Ticks));
                        scanned++;
                        if (batch.Count >= 512)
                        {
                            await database.UpsertBatchAsync(source.Id, generation, batch, ct);
                            batch.Clear();
                            progress?.Report(scanned);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (IOException) { errors++; }
                    catch (UnauthorizedAccessException) { errors++; }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { errors++; }
            catch (UnauthorizedAccessException) { errors++; }
        }

        if (batch.Count > 0) await database.UpsertBatchAsync(source.Id, generation, batch, ct);
        ct.ThrowIfCancellationRequested();

        // A partial traversal is NOT evidence of file deletion.
        if (errors == 0) await database.MarkUnseenAsMissingAsync(source.Id, generation, ct);
        var errorText = errors == 0 ? null : "Encountered " + errors + " filesystem errors; no deletions reconciled.";
        await database.SetSourceStateAsync(source.Id, true, errorText, errors == 0, ct);
        progress?.Report(scanned);
        return new ScanResult(source.Id, scanned, errors, errors == 0, true);
    }
}
