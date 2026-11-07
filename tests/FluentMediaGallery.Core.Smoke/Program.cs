using FluentMediaGallery.Core;

static void Check(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + reason);
    Console.WriteLine("PASS: " + reason);
}

Check(FavoriteRatingPolicy.IsLegacyFavorite(75), "4-star rating 75 imports as favorite");
Check(FavoriteRatingPolicy.IsLegacyFavorite(63), "4-star low boundary 63");
Check(!FavoriteRatingPolicy.IsLegacyFavorite(99), "5 stars do not import as favorites");
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
    var firstPage = await database.GetPageAsync(0, 100);
    var photo = firstPage.Single(x => x.Name == "one.jpg");
    await database.SetFavoriteOverrideAsync(photo.Id, true);
    Check((await database.GetPageAsync(0, 100, favoritesOnly: true)).Count == 1, "favorite override persists");
    Check((await database.GetPageAsync(0, 100, search: "one")).Count == 1, "search works");

    var dbReopened = new LibraryDatabase(database.DatabasePath);
    await dbReopened.InitializeAsync();
    Check((await dbReopened.GetPageAsync(0, 100, favoritesOnly: true)).Count == 1, "restart retains favorite without rescan");

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
