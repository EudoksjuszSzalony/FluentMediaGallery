using Microsoft.Data.Sqlite;

namespace FluentMediaGallery.Core;

public sealed class LibraryDatabase
{
    public string DatabasePath { get; }

    public LibraryDatabase(string? databasePath = null)
    {
        DatabasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FluentMediaGallery", "library.sqlite");
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DatabasePath))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS sources (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                root TEXT NOT NULL COLLATE NOCASE UNIQUE,
                is_online INTEGER NOT NULL DEFAULT 0,
                last_scan_utc TEXT,
                last_error TEXT
            );
            CREATE TABLE IF NOT EXISTS view_preferences (
                view_key TEXT PRIMARY KEY COLLATE NOCASE,
                sort_key TEXT NOT NULL DEFAULT 'modified-desc',
                layout_key TEXT NOT NULL DEFAULT 'square'
            );
            CREATE TABLE IF NOT EXISTS shortcuts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_id INTEGER NOT NULL REFERENCES sources(id),
                folder_path TEXT NOT NULL COLLATE NOCASE UNIQUE
            );
            CREATE INDEX IF NOT EXISTS ix_shortcuts_source ON shortcuts(source_id);
            CREATE TABLE IF NOT EXISTS media (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_id INTEGER NOT NULL REFERENCES sources(id),
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
            CREATE INDEX IF NOT EXISTS ix_media_browse
                ON media(missing, modified_ticks DESC, id DESC);
            CREATE INDEX IF NOT EXISTS ix_media_source
                ON media(source_id, seen_generation);
            CREATE INDEX IF NOT EXISTS ix_media_rating_pending
                ON media(rating_checked, missing, id);
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        // Additive migration for databases created before file-metadata write support.
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(media);";
            await using var reader = await info.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) columns.Add(reader.GetString(1));
        }
        if (!columns.Contains("favorite_write_pending"))
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE media ADD COLUMN favorite_write_pending INTEGER NOT NULL DEFAULT 0;";
            await alter.ExecuteNonQueryAsync(ct);
        }
        if (!columns.Contains("favorite_write_error"))
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE media ADD COLUMN favorite_write_error TEXT;";
            await alter.ExecuteNonQueryAsync(ct);
        }
        if (!columns.Contains("created_ticks"))
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE media ADD COLUMN created_ticks INTEGER NOT NULL DEFAULT 0;";
            await alter.ExecuteNonQueryAsync(ct);
        }
        using (var index = connection.CreateCommand())
        {
            index.CommandText = "CREATE INDEX IF NOT EXISTS ix_media_created ON media(missing, created_ticks DESC, id DESC);";
            await index.ExecuteNonQueryAsync(ct);
        }
    }


    private static string NormalizeFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Folder path cannot be empty.", nameof(path));
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        if (!Path.IsPathFullyQualified(full))
            throw new ArgumentException("Folder must be an absolute path.", nameof(path));
        return Path.TrimEndingDirectorySeparator(full);
    }

    private static bool IsSameOrDescendant(string path, string root)
    {
        return path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// If folder is nested under an existing root, register a shortcut, NOT a second
    /// index source. A new ancestor of an existing root is refused to avoid duplicate scans.
    /// Does not touch the source filesystem or scan anything.
    /// </summary>
    public async Task<MediaLocation> RegisterLocationAsync(string folderPath,
        CancellationToken ct = default)
    {
        var folder = NormalizeFolder(folderPath);
        var sources = await ListSourcesAsync(ct);
        foreach (var source in sources)
        {
            if (folder.Equals(source.Root, StringComparison.OrdinalIgnoreCase))
                return new MediaLocation(source.Id, folder, false, false);
            if (!IsSameOrDescendant(folder, source.Root)) continue;

            await using var connection = await OpenAsync(ct);
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO shortcuts(source_id,folder_path) VALUES($source,$folder)
                ON CONFLICT(folder_path) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$source", source.Id);
            insert.Parameters.AddWithValue("$folder", folder);
            var created = await insert.ExecuteNonQueryAsync(ct) != 0;
            return new MediaLocation(source.Id, folder, true, created);
        }

        if (sources.Any(s => IsSameOrDescendant(s.Root, folder)))
            throw new InvalidOperationException(
                "This folder contains an already indexed source. Remove or consolidate the existing source first; duplicate scanning is blocked.");

        var added = await AddSourceAsync(folder, ct);
        return new MediaLocation(added.Id, folder, false, true);
    }

    public async Task<IReadOnlyList<MediaLocation>> ListShortcutsAsync(CancellationToken ct = default)
    {
        var items = new List<MediaLocation>();
        await using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT source_id,folder_path FROM shortcuts ORDER BY folder_path COLLATE NOCASE;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(new MediaLocation(reader.GetInt64(0), reader.GetString(1), true, false));
        return items;
    }

    /// <summary>
    /// Lists immediate children having at least one indexed media item; makes no filesystem calls
    /// and remains useful when the selected NAS is temporarily offline.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetIndexedChildFoldersAsync(long sourceId,
        string parentPath, CancellationToken ct = default)
    {
        var parent = NormalizeFolder(parentPath);
        var prefix = parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var children = new List<string>();
        await using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT substr(path, 1,
                length($prefix) + instr(substr(path, length($prefix)+1), '\') - 1) AS child
            FROM media
            WHERE source_id=$source AND missing=0
              AND path LIKE $like ESCAPE '\'
              AND instr(substr(path, length($prefix)+1), '\') > 0
            ORDER BY child COLLATE NOCASE LIMIT 1000;
            """;
        cmd.Parameters.AddWithValue("$source", sourceId);
        cmd.Parameters.AddWithValue("$prefix", prefix);
        cmd.Parameters.AddWithValue("$like", EscapeLike(prefix) + "%");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) children.Add(reader.GetString(0));
        return children;
    }

    public async Task<LibrarySource> AddSourceAsync(string sourcePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("Source path cannot be empty.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Environment.ExpandEnvironmentVariables(sourcePath.Trim())));
        await using var connection = await OpenAsync(ct);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO sources(root) VALUES($root) ON CONFLICT(root) DO NOTHING;";
            command.Parameters.AddWithValue("$root", root);
            await command.ExecuteNonQueryAsync(ct);
        }
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT id,root,is_online,last_scan_utc,last_error FROM sources WHERE root=$root;";
        query.Parameters.AddWithValue("$root", root);
        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Source registration failed.");
        return ReadSource(reader);
    }

    public async Task<IReadOnlyList<LibrarySource>> ListSourcesAsync(CancellationToken ct = default)
    {
        var result = new List<LibrarySource>();
        await using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id,root,is_online,last_scan_utc,last_error FROM sources ORDER BY id;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadSource(reader));
        return result;
    }

    private static LibrarySource ReadSource(SqliteDataReader row) => new(
        row.GetInt64(0), row.GetString(1), row.GetInt64(2) != 0,
        row.IsDBNull(3) ? null : row.GetString(3),
        row.IsDBNull(4) ? null : row.GetString(4));

    public async Task SetSourceStateAsync(long sourceId, bool online, string? error, bool completed, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE sources SET is_online=$online, last_error=$error,
            last_scan_utc=CASE WHEN $completed=1 THEN $utc ELSE last_scan_utc END WHERE id=$id;
            """;
        cmd.Parameters.AddWithValue("$online", online ? 1 : 0);
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$completed", completed ? 1 : 0);
        cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", sourceId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertBatchAsync(long sourceId, long generation, IReadOnlyList<MediaFile> files, CancellationToken ct = default)
    {
        if (files.Count == 0) return;
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)transaction;
        cmd.CommandText = """
            INSERT INTO media(source_id,path,file_name,kind,size,modified_ticks,created_ticks,seen_generation,missing)
            VALUES($source,$path,$name,$kind,$size,$ticks,$created,$gen,0)
            ON CONFLICT(path) DO UPDATE SET
                source_id=excluded.source_id,
                file_name=excluded.file_name,
                kind=excluded.kind,
                rating_raw=CASE WHEN media.size=excluded.size AND media.modified_ticks=excluded.modified_ticks
                    THEN media.rating_raw ELSE NULL END,
                rating_checked=CASE WHEN media.size=excluded.size AND media.modified_ticks=excluded.modified_ticks
                    THEN media.rating_checked ELSE 0 END,
                size=excluded.size,
                modified_ticks=excluded.modified_ticks,
                created_ticks=excluded.created_ticks,
                seen_generation=excluded.seen_generation,
                missing=0;
            """;
        var source = cmd.Parameters.Add("$source", SqliteType.Integer);
        var path = cmd.Parameters.Add("$path", SqliteType.Text);
        var name = cmd.Parameters.Add("$name", SqliteType.Text);
        var kind = cmd.Parameters.Add("$kind", SqliteType.Integer);
        var size = cmd.Parameters.Add("$size", SqliteType.Integer);
        var ticks = cmd.Parameters.Add("$ticks", SqliteType.Integer);
        var created = cmd.Parameters.Add("$created", SqliteType.Integer);
        var gen = cmd.Parameters.Add("$gen", SqliteType.Integer);
        cmd.Prepare();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            source.Value = sourceId;
            path.Value = file.FullPath;
            name.Value = file.Name;
            kind.Value = (int)file.Kind;
            size.Value = file.Size;
            ticks.Value = file.LastWriteUtcTicks;
            created.Value = file.CreatedUtcTicks;
            gen.Value = generation;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    // ONLY invoke after a fully successful traversal; never after cancellation, offline, or permission errors.
    public async Task MarkUnseenAsMissingAsync(long sourceId, long generation, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE media SET missing=1 WHERE source_id=$source AND seen_generation<>$gen;";
        cmd.Parameters.AddWithValue("$source", sourceId);
        cmd.Parameters.AddWithValue("$gen", generation);
        await cmd.ExecuteNonQueryAsync(ct);
    }


    private static string ResolveOrderBy(string? sortKey) => sortKey switch
    {
        "modified-asc" => "modified_ticks ASC, id ASC",
        "created-desc" => "CASE WHEN created_ticks=0 THEN modified_ticks ELSE created_ticks END DESC, id DESC",
        "created-asc" => "CASE WHEN created_ticks=0 THEN modified_ticks ELSE created_ticks END ASC, id ASC",
        "name-asc" => "file_name COLLATE NOCASE ASC, id ASC",
        "name-desc" => "file_name COLLATE NOCASE DESC, id DESC",
        "rating-desc" => "COALESCE(rating_raw,0) DESC, modified_ticks DESC, id DESC",
        "rating-asc" => "COALESCE(rating_raw,0) ASC, modified_ticks DESC, id DESC",
        "favorites-desc" => "COALESCE(favorite_override,CASE WHEN rating_raw BETWEEN 63 AND 99 THEN 1 ELSE 0 END) DESC, modified_ticks DESC, id DESC",
        "favorites-asc" => "COALESCE(favorite_override,CASE WHEN rating_raw BETWEEN 63 AND 99 THEN 1 ELSE 0 END) ASC, modified_ticks DESC, id DESC",
        _ => "modified_ticks DESC, id DESC"
    };

    public async Task<(string SortKey, string LayoutKey)> GetViewPreferenceAsync(
        string viewKey, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sort_key,layout_key FROM view_preferences WHERE view_key=$key;";
        cmd.Parameters.AddWithValue("$key", viewKey);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct)) return (reader.GetString(0), reader.GetString(1));
        return ("modified-desc", "square");
    }

    public async Task SetViewPreferenceAsync(string viewKey, string sortKey,
        string layoutKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(viewKey)) throw new ArgumentException("View key required.", nameof(viewKey));
        // Only store known allow-listed settings; no user input is ever interpolated into SQL.
        if (sortKey != "modified-desc" && ResolveOrderBy(sortKey) == ResolveOrderBy(null))
            throw new ArgumentOutOfRangeException(nameof(sortKey));
        if (layoutKey is not ("square" or "river"))
            throw new ArgumentOutOfRangeException(nameof(layoutKey));
        await using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO view_preferences(view_key,sort_key,layout_key)
            VALUES($key,$sort,$layout)
            ON CONFLICT(view_key) DO UPDATE SET sort_key=excluded.sort_key,layout_key=excluded.layout_key;
            """;
        cmd.Parameters.AddWithValue("$key", viewKey);
        cmd.Parameters.AddWithValue("$sort", sortKey);
        cmd.Parameters.AddWithValue("$layout", layoutKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<MediaEntry>> GetPageAsync(int offset, int limit,
        bool favoritesOnly = false, string? search = null, CancellationToken ct = default,
        long? sourceId = null, string? folderPath = null, string? sortKey = null)
    {
        if (offset < 0 || limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        var result = new List<MediaEntry>();
        await using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, source_id, path, file_name, kind, size, modified_ticks, rating_raw,
                favorite_override,
                COALESCE(favorite_override, CASE WHEN rating_raw BETWEEN 63 AND 99 THEN 1 ELSE 0 END) AS fav,
                created_ticks
            FROM media WHERE missing=0
                AND ($source=0 OR source_id=$source)
                AND ($folder='' OR path LIKE $folder ESCAPE '\')
                AND ($favorites=0 OR COALESCE(favorite_override,
                    CASE WHEN rating_raw BETWEEN 63 AND 99 THEN 1 ELSE 0 END)=1)
                AND ($search='' OR file_name LIKE $search ESCAPE '\')
            ORDER BY SORT_EXPRESSION LIMIT $limit OFFSET $offset;
            """.Replace("SORT_EXPRESSION", ResolveOrderBy(sortKey), StringComparison.Ordinal);
        cmd.Parameters.AddWithValue("$source", sourceId.GetValueOrDefault());
        cmd.Parameters.AddWithValue("$folder", folderPath is null ? "" :
            EscapeLike(NormalizeFolder(folderPath).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar) + "%");
        cmd.Parameters.AddWithValue("$favorites", favoritesOnly ? 1 : 0);
        cmd.Parameters.AddWithValue("$search", string.IsNullOrWhiteSpace(search) ? "" :
            "%" + search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new MediaEntry(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.GetString(3), (MediaKind)reader.GetInt32(4),
                reader.GetInt64(5), reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8) != 0,
                reader.GetInt64(9) != 0,
                reader.GetInt64(10)));
        }
        return result;
    }

    public async Task<MediaEntry?> GetByIdAsync(long mediaId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id,source_id,path,file_name,kind,size,modified_ticks,rating_raw,
                favorite_override,
                COALESCE(favorite_override,CASE WHEN rating_raw BETWEEN 63 AND 99 THEN 1 ELSE 0 END)
            FROM media WHERE id=$id;
            """;
        cmd.Parameters.AddWithValue("$id", mediaId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new MediaEntry(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
            reader.GetString(3), (MediaKind)reader.GetInt32(4), reader.GetInt64(5),
            reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetInt32(8) != 0, reader.GetInt64(9) != 0);
    }

    // This persists the user's choice first. File metadata synchronization is handled by FavoritesService.
    public async Task SetFavoriteOverrideAsync(long mediaId, bool? favorite, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE media SET favorite_override=$fav,
                favorite_write_pending=CASE WHEN $fav IS NULL THEN 0 ELSE 1 END,
                favorite_write_error=NULL
            WHERE id=$id;
            """;
        cmd.Parameters.AddWithValue("$fav", favorite is null ? DBNull.Value : favorite.Value ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", mediaId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<FavoriteWriteCandidate>> GetPendingFavoriteWritesAsync(
        int limit = 32, CancellationToken ct = default)
    {
        var result = new List<FavoriteWriteCandidate>();
        await using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT m.id,m.path,m.favorite_override FROM media m
            JOIN sources s ON s.id=m.source_id
            WHERE m.favorite_write_pending=1 AND m.favorite_override IS NOT NULL
              AND m.missing=0 AND s.is_online=1
            ORDER BY m.id LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 128));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new FavoriteWriteCandidate(reader.GetInt64(0), reader.GetString(1),
                reader.GetInt32(2) != 0));
        return result;
    }

    public async Task CompleteFavoriteFileWriteAsync(long mediaId, bool expectedFavorite,
        int persistedRating, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE media SET rating_raw=$rating, rating_checked=1,
                favorite_write_pending=0, favorite_write_error=NULL
            WHERE id=$id AND favorite_override=$expected;
            """;
        cmd.Parameters.AddWithValue("$id", mediaId);
        cmd.Parameters.AddWithValue("$expected", expectedFavorite ? 1 : 0);
        cmd.Parameters.AddWithValue("$rating", persistedRating);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordFavoriteFileWriteErrorAsync(long mediaId, bool expectedFavorite,
        string reason, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE media SET favorite_write_pending=1, favorite_write_error=$reason
            WHERE id=$id AND favorite_override=$expected;
            """;
        cmd.Parameters.AddWithValue("$id", mediaId);
        cmd.Parameters.AddWithValue("$expected", expectedFavorite ? 1 : 0);
        cmd.Parameters.AddWithValue("$reason", reason.Length > 500 ? reason[..500] : reason);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(bool Pending, string? Error)> GetFavoriteWriteStateAsync(
        long mediaId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT favorite_write_pending,favorite_write_error FROM media WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", mediaId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Unknown media id: " + mediaId);
        return (reader.GetInt64(0) != 0, reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    public async Task<IReadOnlyList<RatingCandidate>> GetPendingRatingsAsync(int limit = 64, CancellationToken ct = default,
        long? sourceId = null)
    {
        var result = new List<RatingCandidate>();
        await using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT m.id,m.path FROM media m JOIN sources s ON m.source_id=s.id
            WHERE m.rating_checked=0 AND m.missing=0 AND s.is_online=1
              AND ($source=0 OR m.source_id=$source)
            ORDER BY m.id LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$source", sourceId.GetValueOrDefault());
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 256));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new RatingCandidate(reader.GetInt64(0), reader.GetString(1)));
        return result;
    }

    public async Task SaveFileRatingAsync(long mediaId, int? rating, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE media SET rating_raw=$rating,rating_checked=1 WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", mediaId);
        cmd.Parameters.AddWithValue("$rating", rating.HasValue ? rating.Value : DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }


    /// <summary>
    /// Counts rows matching the same filters as GetPageAsync without loading media objects.
    /// </summary>
    public async Task<(long Minimum, long Maximum)> GetDateRangeAsync(long? sourceId = null,
        bool favoritesOnly = false, string? search = null,
        string? folderPath = null, bool creationDate = false,
        CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        var column = creationDate
            ? "CASE WHEN created_ticks=0 THEN modified_ticks ELSE created_ticks END"
            : "modified_ticks";
        cmd.CommandText = $"""
            SELECT MIN({column}),MAX({column}) FROM media WHERE missing=0
              AND ($source=0 OR source_id=$source)
              AND ($folder='' OR path LIKE $folder ESCAPE '\')
              AND ($favorites=0 OR COALESCE(favorite_override,
                CASE WHEN rating_raw BETWEEN 63 AND 99 THEN 1 ELSE 0 END)=1)
              AND ($search='' OR file_name LIKE $search ESCAPE '\');
            """;
        cmd.Parameters.AddWithValue("$source", sourceId.GetValueOrDefault());
        cmd.Parameters.AddWithValue("$folder", folderPath is null ? "" :
            EscapeLike(NormalizeFolder(folderPath).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar) + "%");
        cmd.Parameters.AddWithValue("$favorites", favoritesOnly ? 1 : 0);
        cmd.Parameters.AddWithValue("$search", string.IsNullOrWhiteSpace(search) ? "" :
            "%" + EscapeLike(search) + "%");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(0)) return (0, 0);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    public async Task<long> CountFilteredAsync(long? sourceId = null,
        bool favoritesOnly = false, string? search = null, CancellationToken ct = default,
        string? folderPath = null)
    {
        await using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM media
            WHERE missing=0 AND ($source=0 OR source_id=$source)
              AND ($folder='' OR path LIKE $folder ESCAPE '\')
              AND ($favorites=0 OR COALESCE(favorite_override,
                    CASE WHEN rating_raw BETWEEN 63 AND 99 THEN 1 ELSE 0 END)=1)
              AND ($search='' OR file_name LIKE $search ESCAPE '\');
            """;
        cmd.Parameters.AddWithValue("$source", sourceId.GetValueOrDefault());
        cmd.Parameters.AddWithValue("$folder", folderPath is null ? "" :
            EscapeLike(NormalizeFolder(folderPath).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar) + "%");
        cmd.Parameters.AddWithValue("$favorites", favoritesOnly ? 1 : 0);
        cmd.Parameters.AddWithValue("$search", string.IsNullOrWhiteSpace(search) ? "" :
            "%" + search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }

    public async Task<long> CountMediaAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM media WHERE missing=0;";
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }
}
