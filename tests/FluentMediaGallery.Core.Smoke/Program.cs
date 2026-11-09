using FluentMediaGallery.Core;

static void Check(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + reason);
    Console.WriteLine("PASS: " + reason);
}

Check(GalleryWindowPolicy.BatchSize == 100, "bounded gallery query batch remains 100");
Check(GalleryWindowPolicy.MaxRetainedMedia == 400, "gallery keeps at most four media batches");
Check(GalleryWindowPolicy.TrimExcess(400, 100) == 100,
    "scroll forward evicts the oldest full media batch");
Check(GalleryWindowPolicy.TrimExcess(300, 100) == 0,
    "scroll forward retains batches below bound");
Check(GalleryWindowPolicy.TrimExcess(399, 100) == 100,
    "window pruning happens in full batches");
Check(GalleryWindowPolicy.PreviousBatchOffset(500) == 400 &&
      GalleryWindowPolicy.PreviousBatchOffset(0) == 0,
    "scroll backward never requests a negative SQLite offset");

Check(FavoriteRatingPolicy.IsLegacyFavorite(75), "4-star rating 75 imports as favorite");
Check(FavoriteRatingPolicy.ToStars(75) == 4, "75 raw rating displays four stars");
Check(FavoriteRatingPolicy.ToStars(99) == 5, "99 raw rating displays five stars");
Check(FavoriteRatingPolicy.ToStars(1) == 1, "1 raw rating displays one star");
Check(FavoriteRatingPolicy.IsLegacyFavorite(63), "4-star low boundary 63");
Check(FavoriteRatingPolicy.IsLegacyFavorite(99), "5 stars import as favorites");
Check(FavoriteRatingPolicy.IsLegacyFavorite(88), "5 stars lower bound imports as favorites");
Check(!FavoriteRatingPolicy.IsLegacyFavorite(62), "3 stars not favorite");
Check(!FavoriteRatingPolicy.IsLegacyFavorite(1), "1 star not favorite");
Check(FavoriteRatingPolicy.IsFavorite(true, 1), "override true wins over file");
Check(!FavoriteRatingPolicy.IsFavorite(false, 75), "override false wins over file");
Check(MediaFormats.Identify(@"\\NAS\Photos\video.MKV") == MediaKind.Video, "UNC videos are recognized");

var temp = Path.Combine(Path.GetTempPath(), "FluentMediaGallery-Smoke-" + Guid.NewGuid().ToString("N"));
var root = Path.Combine(temp, "Media");
Directory.CreateDirectory(root);
try
{
    var database = new LibraryDatabase(Path.Combine(temp, "state", "library.sqlite"));
    await database.InitializeAsync();
    var source = await database.AddSourceAsync(root);
    File.WriteAllText(Path.Combine(root, "one.jpg"), "fixture");
    File.WriteAllText(Path.Combine(root, "two.mkv"), "fixture");

    var scanner = new DirectoryIndexer(database);
    var scan = await scanner.ScanAsync(source);
    Check(scan.Completed && scan.Discovered == 2, "initial scan stores photo and video");
    Check(await database.CountMediaAsync() == 2, "two indexed media");
    var range = await database.GetDateRangeAsync(sourceId: source.Id);
    Check(range.Maximum >= range.Minimum && range.Maximum > 0,
        "gallery timeline range comes from indexed dates");
    Check((await database.GetDateRangeAsync(sourceId: -123)).Maximum == 0,
        "empty gallery timeline reports no dates");
    Check((await database.GetPendingRatingsAsync(sourceId: source.Id)).Count == 2,
        "source-scoped pending rating import includes both files");
    Check((await database.GetPendingRatingsAsync(sourceId: -999)).Count == 0,
        "source-scoped rating import excludes another source");
    Check(await database.CountFilteredAsync(sourceId: source.Id) == 2,
        "source filter returns both indexed files");
    Check(await database.CountFilteredAsync(sourceId: -999) == 0,
        "non-existent source is empty");
    Check((await database.GetPageAsync(0, 100, sourceId: source.Id)).Count == 2,
        "paged source filter works");
    await database.SetViewPreferenceAsync("folder:" + root, "name-desc", "river");
    var prefs = await database.GetViewPreferenceAsync("folder:" + root);
    Check(prefs.SortKey == "name-desc" && prefs.LayoutKey == "river", "sort/layout preference persists");
    Check((await database.GetViewPreferenceAsync("folder:" + root + "\\other")).SortKey == "modified-desc",
        "nested folder has independent default sort");
    Check((await database.GetPageAsync(0, 100, sourceId: source.Id, sortKey: "name-desc"))[0].Name == "two.mkv",
        "name-desc ordering works");
    Check((await database.GetPageAsync(0, 100, sourceId: source.Id, sortKey: "name-asc"))[0].Name == "one.jpg",
        "name-asc ordering works");
    Check((await database.GetPageAsync(0, 100, sourceId: -999)).Count == 0,
        "paged source exclusion works");
    Check(await database.CountFilteredAsync(search: "one") == 1,
        "filtered count follows search");
    var firstPage = await database.GetPageAsync(0, 100);
    var photo = firstPage.Single(x => x.Name == "one.jpg");
    Check(photo.PixelWidth == 0 && photo.PixelHeight == 0,
        "dimensions start uncached without source file decoding");
    Check(await database.CacheImageDimensionsAsync(new[]
    {
        new MediaDimension(photo.Id, photo.LastWriteUtcTicks - 1, photo.Size, 1920, 1080),
        new MediaDimension(photo.Id, photo.LastWriteUtcTicks, photo.Size, 1920, 1080)
    }) == 1, "stale dimension probes are rejected");
    Check((await database.GetByIdAsync(photo.Id))?.PixelWidth == 1920,
        "image width and height cached in SQLite");
    Check((await database.GetPageAsync(0, 100)).Single(x => x.Id == photo.Id).PixelHeight == 1080,
        "gallery pagination returns persisted dimensions");
    await scanner.ScanAsync(source);
    Check((await database.GetByIdAsync(photo.Id))?.PixelWidth == 1920,
        "unchanged rescan preserves cached image dimensions");

    await database.SetFavoriteOverrideAsync(photo.Id, true);
    Check((await database.GetPageAsync(0, 100, favoritesOnly: true)).Count == 1, "favorite override persists");
    Check(await database.CountFilteredAsync(favoritesOnly: true) == 1,
        "filtered favorites count follows override");
    Check((await database.GetFavoriteWriteStateAsync(photo.Id)).Pending, "metadata write queued");
    await database.RecordFavoriteFileWriteErrorAsync(photo.Id, true, "not writable");
    Check((await database.GetFavoriteWriteStateAsync(photo.Id)).Error == "not writable",
        "metadata error is retained without losing favorite");
    Check((await database.GetPendingFavoriteWritesAsync()).Count == 1, "pending metadata synchronization tracked");
    await database.CompleteFavoriteFileWriteAsync(photo.Id, true, 99);
    Check(!(await database.GetFavoriteWriteStateAsync(photo.Id)).Pending, "success clears pending metadata");
    Check((await database.GetByIdAsync(photo.Id))?.FileRating == 99, "5-star rating stored in index");
    await database.SetFavoriteOverrideAsync(photo.Id, null);
    Check((await database.GetPageAsync(0, 100, favoritesOnly: true)).Count == 1,
        "five-star unoverridden file appears in favorites filter");
    await database.SetFavoriteOverrideAsync(photo.Id, true);
    await database.CompleteFavoriteFileWriteAsync(photo.Id, true, 99);
    Check((await database.GetPageAsync(0, 100, search: "one")).Count == 1, "search works");

    var dbReopened = new LibraryDatabase(database.DatabasePath);
    await dbReopened.InitializeAsync();
    Check((await dbReopened.GetPageAsync(0, 100, favoritesOnly: true)).Count == 1, "restart retains favorite without rescan");
    await dbReopened.SetFavoriteOverrideAsync(photo.Id, false);
    Check((await dbReopened.GetPageAsync(0, 100, favoritesOnly: true)).Count == 0, "unfavorite overrides 5-star file");
    await dbReopened.CompleteFavoriteFileWriteAsync(photo.Id, false, 1);
    Check((await dbReopened.GetByIdAsync(photo.Id))?.FileRating == 1, "unfavorite stores 1 star");
    await dbReopened.SetFavoriteOverrideAsync(photo.Id, null);
    Check((await dbReopened.GetByIdAsync(photo.Id))?.IsFavorite == false, "clear override inherits one-star rating");
    await dbReopened.SetFavoriteOverrideAsync(photo.Id, true);
    await dbReopened.CompleteFavoriteFileWriteAsync(photo.Id, true, 75);

    // Nested folders are virtual shortcuts: one source, one physical index.
    var nested = Path.Combine(root, "nested");
    Directory.CreateDirectory(nested);
    File.WriteAllText(Path.Combine(nested, "inside.jpg"), "fixture");
    var nestedRegistration = await database.RegisterLocationAsync(nested);
    Check(nestedRegistration.IsShortcut && nestedRegistration.IsNew,
        "adding nested folder creates a shortcut instead of a second source");
    Check((await database.RegisterLocationAsync(nested)).IsShortcut,
        "registering the same nested path stays a shortcut");
    Check((await database.ListSourcesAsync()).Count == 1, "no duplicated index source");
    Check((await database.ListShortcutsAsync()).Count == 1, "shortcut persisted in SQLite");
    await scanner.ScanAsync(source);
    Check((await database.GetIndexedChildFoldersAsync(source.Id, root))
        .Any(p => p.Equals(nested, StringComparison.OrdinalIgnoreCase)), "child folder exists in index");
    Check((await database.GetPageAsync(0, 100, sourceId: source.Id, folderPath: nested)).Count == 1,
        "folder view returns only descendants");
    Check(await database.CountFilteredAsync(sourceId: source.Id, folderPath: nested) == 1,
        "folder count matches folder-filtered pagination");
    Check((await database.GetPageAsync(0, 100, sourceId: source.Id)).Count == 3,
        "shortcut does not duplicate media");
    Directory.Delete(nested, true);
    await scanner.ScanAsync(source);

    File.Delete(Path.Combine(root, "two.mkv"));
    await scanner.ScanAsync(source);
    Check(await database.CountMediaAsync() == 1, "successful rescan marks disappeared video as missing");

    Directory.Delete(root, true);
    var offlineScan = await scanner.ScanAsync(source);
    Check(!offlineScan.Online && !offlineScan.Completed, "offline source reported");
    Check(await database.CountMediaAsync() == 1, "offline source does not wipe catalogue");
    Check((await database.ListSourcesAsync()).Single().IsOnline == false, "source offline status retained");

    Directory.CreateDirectory(root);
    File.WriteAllText(Path.Combine(root, "one.jpg"), "fixture");
    File.WriteAllText(Path.Combine(root, "two.mkv"), "fixture");
    await scanner.ScanAsync(source);
    Check(await database.CountMediaAsync() == 2, "returning offline source reconnects");
    Check((await database.GetPageAsync(0, 100, favoritesOnly: true)).Count == 1, "favorite survives offline and reconnect");

    // A separate read-only fixture for pagination; don't change the favorites
    // or filesystem scans above. SQLite can index media without opening files.

    // Reproduce an older user database WITHOUT the new width/height columns.
    // Check the additive migration before touching any real Dominator library.
    var legacyPath = Path.Combine(temp, "Legacy", "library.sqlite");
    Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
    await using (var legacyConnection =
        new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + legacyPath))
    {
        await legacyConnection.OpenAsync();
        using var cmd = legacyConnection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE media (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_id INTEGER NOT NULL,
                path TEXT NOT NULL COLLATE NOCASE UNIQUE,
                file_name TEXT NOT NULL,
                kind INTEGER NOT NULL,
                size INTEGER NOT NULL,
                modified_ticks INTEGER NOT NULL,
                created_ticks INTEGER NOT NULL DEFAULT 0,
                seen_generation INTEGER NOT NULL DEFAULT 0,
                missing INTEGER NOT NULL DEFAULT 0,
                rating_raw INTEGER,
                rating_checked INTEGER NOT NULL DEFAULT 0,
                favorite_override INTEGER,
                favorite_write_pending INTEGER NOT NULL DEFAULT 0,
                favorite_write_error TEXT
            );
            INSERT INTO media(source_id,path,file_name,kind,size,modified_ticks,
                created_ticks,missing,rating_raw,rating_checked)
            VALUES(1,$path,'existing.jpg',1,4096,$ticks,0,0,75,1);
            """;
        cmd.Parameters.AddWithValue("$path", Path.Combine(temp, "Legacy", "existing.jpg"));
        cmd.Parameters.AddWithValue("$ticks", DateTime.UtcNow.Ticks);
        await cmd.ExecuteNonQueryAsync();
    }
    var legacy = new LibraryDatabase(legacyPath);
    await legacy.InitializeAsync();
    var previous = (await legacy.GetPageAsync(0, 100)).Single();
    Check(previous.Name == "existing.jpg" && previous.IsFavorite &&
          previous.FileRating == 75 && previous.PixelWidth == 0,
        "existing SQLite library migrates without losing original favorites");
    await legacy.CacheImageDimensionsAsync(new[]
    {
        new MediaDimension(previous.Id, previous.LastWriteUtcTicks,
            previous.Size, 1200, 800)
    });
    await legacy.InitializeAsync();
    Check((await legacy.GetByIdAsync(previous.Id)) is
        { PixelWidth: 1200, PixelHeight: 800, IsFavorite: true },
        "additive dimension migration is idempotent and preserves favorites");

    var pagingDb = new LibraryDatabase(Path.Combine(temp, "Paging", "library.sqlite"));
    await pagingDb.InitializeAsync();
    var pagingSource = await pagingDb.AddSourceAsync(Path.Combine(temp, "VirtualMedia"));
    var synthetic = Enumerable.Range(0, 650).Select(i => new MediaFile(
        Path.Combine(temp, "VirtualMedia", $"{i:0000}.jpg"),
        $"{i:0000}.jpg", MediaKind.Photo, 200 + i,
        new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks + i)).ToArray();
    await pagingDb.UpsertBatchAsync(pagingSource.Id, 1, synthetic);
    var baseTicks = new DateTime(2025, 1, 1, 0, 0, 0,
        DateTimeKind.Utc).Ticks;
    Check(await pagingDb.GetOffsetForDateAsync(baseTicks + 500,
        sourceId: pagingSource.Id) == 149,
        "date rail seeks by real dates in descending sort");
    Check(await pagingDb.GetOffsetForDateAsync(baseTicks + 500,
        ascending: true, sourceId: pagingSource.Id) == 500,
        "date rail seeks by real dates in ascending sort");
    Check(await pagingDb.GetOffsetForDateAsync(baseTicks + 500,
        sourceId: -999) == 0,
        "date rail respects user-selected source scope");

    var head = await pagingDb.GetPageAsync(0, GalleryWindowPolicy.BatchSize);
    var tail = await pagingDb.GetPageAsync(600, GalleryWindowPolicy.BatchSize);
    Check(head.Count == 100 && head[0].Name == "0649.jpg",
        "paged SQLite browsing starts with newest indexed file");
    Check(tail.Count == 50 && tail[^1].Name == "0000.jpg",
        "partial last batch returns the final media without duplication");
    Check((await pagingDb.GetPageAsync(100, 100))[0].Id != head[0].Id,
        "consecutive pages select distinct indexed media");
    var retained = 0;
    var windowStart = 0;
    for (var loaded = 0; loaded < 650; loaded += GalleryWindowPolicy.BatchSize)
    {
        var incoming = Math.Min(GalleryWindowPolicy.BatchSize, 650 - loaded);
        var prune = Math.Min(retained, GalleryWindowPolicy.TrimExcess(retained, incoming));
        windowStart += prune;
        retained += incoming - prune;
        Check(retained <= GalleryWindowPolicy.MaxRetainedMedia,
            "gallery window stays within fixed media memory budget");
    }
    Check(windowStart == 300 && retained == 350 && windowStart + retained == 650,
        "forward scrolling keeps an exact continuous absolute offset");
    var earlier = GalleryWindowPolicy.PreviousBatchOffset(windowStart);
    Check(earlier == 200 &&
        (await pagingDb.GetPageAsync(earlier, 100)).Count == 100,
        "reverse scrolling reloads an evicted SQLite batch");

}
finally
{
    // SQLite connection pooling may retain handles briefly. Never fail smoke checks on cleanup.
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { Directory.Delete(temp, true); } catch (IOException) { }
}
Console.WriteLine("ALL CORE SMOKE TESTS PASSED");
