using FluentMediaGallery.Core;

static void Check(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + reason);
    Console.WriteLine("PASS: " + reason);
}

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
}
finally
{
    // SQLite connection pooling may retain handles briefly. Never fail smoke checks on cleanup.
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { Directory.Delete(temp, true); } catch (IOException) { }
}
Console.WriteLine("ALL CORE SMOKE TESTS PASSED");
