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
            CREATE TABLE IF NOT EXISTS media (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_id INTEGER NOT NULL REFERENCES sources(id),
                path TEXT NOT NULL COLLATE NOCASE UNIQUE,
                file_name TEXT NOT NULL,
                kind INTEGER NOT NULL,
                size INTEGER NOT NULL,
                modified_ticks INTEGER NOT NULL,
                seen_generation INTEGER NOT NULL DEFAULT 0,
                missing INTEGER NOT NULL DEFAULT 0,
                rating_raw INTEGER,
                rating_checked INTEGER NOT NULL DEFAULT 0,
                favorite_override INTEGER
            );
            CREATE INDEX IF NOT EXISTS ix_media_browse
                ON media(missing, modified_ticks DESC, id DESC);
            CREATE INDEX IF NOT EXISTS ix_media_source
                ON media(source_id, seen_generation);
            CREATE INDEX IF NOT EXISTS ix_media_rating_pending
                ON media(rating_checked, missing, id);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
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
            INSERT INTO media(source_id,path,file_name,kind,size,modified_ticks,seen_generation,missing)
            VALUES($source,$path,$name,$kind,$size,$ticks,$gen,0)
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
                seen_generation=excluded.seen_generation,
                missing=0;
            """;
        var source = cmd.Parameters.Add("$source", SqliteType.Integer);
        var path = cmd.Parameters.Add("$path", SqliteType.Text);
        var name = cmd.Parameters.Add("$name", SqliteType.Text);
        var kind = cmd.Parameters.Add("$kind", SqliteType.Integer);
        var size = cmd.Parameters.Add("$size", SqliteType.Integer);
        var ticks = cmd.Parameters.Add("$ticks", SqliteType.Integer);
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

    public async Task<IReadOnlyList<MediaEntry>> GetPageAsync(int offset, int limit,
        bool favoritesOnly = false, string? search = null, CancellationToken ct = default)
    {
        if (offset < 0 || limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        var result = new List<MediaEntry>();
        await using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, source_id, path, file_name, kind, size, modified_ticks, rating_raw,
                favorite_override,
                COALESCE(favorite_override, CASE WHEN rating_raw BETWEEN 63 AND 87 THEN 1 ELSE 0 END) AS fav
            FROM media WHERE missing=0
                AND ($favorites=0 OR COALESCE(favorite_override,
                    CASE WHEN rating_raw BETWEEN 63 AND 87 THEN 1 ELSE 0 END)=1)
                AND ($search='' OR file_name LIKE $search ESCAPE '\')
            ORDER BY modified_ticks DESC, id DESC LIMIT $limit OFFSET $offset;
            """;
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
                reader.GetInt64(9) != 0));
        }
        return result;
    }

    public async Task SetFavoriteOverrideAsync(long mediaId, bool? favorite, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE media SET favorite_override=$fav WHERE id=$id;";
        cmd.Parameters.AddWithValue("$fav", favorite is null ? DBNull.Value : favorite.Value ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", mediaId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<RatingCandidate>> GetPendingRatingsAsync(int limit = 64, CancellationToken ct = default)
    {
        var result = new List<RatingCandidate>();
        await using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT m.id,m.path FROM media m JOIN sources s ON m.source_id=s.id
            WHERE m.rating_checked=0 AND m.missing=0 AND s.is_online=1
            ORDER BY m.id LIMIT $limit;
            """;
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

    public async Task<long> CountMediaAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM media WHERE missing=0;";
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }
}
