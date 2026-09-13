using Microsoft.Data.Sqlite;
using StreamCrate.Core.Models;

namespace StreamCrate.Infrastructure.Storage;

public interface IHistoryStore
{
    Task SaveAsync(HistoryEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HistoryEntry>> SearchAsync(string? query, DownloadJobState? state, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed class SqliteHistoryStore(string path) : IHistoryStore
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
    private readonly string _path = path;

    public async Task SaveAsync(HistoryEntry entry, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO history (id, extractor, media_id, title, source_url, format, quality, output_path, state, error_category, created_at, thumbnail_url)
            VALUES ($id, $extractor, $mediaId, $title, $sourceUrl, $format, $quality, $outputPath, $state, $errorCategory, $createdAt, $thumbnailUrl)
            ON CONFLICT(id) DO UPDATE SET
                state = excluded.state,
                error_category = excluded.error_category,
                output_path = excluded.output_path,
                thumbnail_url = excluded.thumbnail_url;
            """;
        command.Parameters.AddWithValue("$id", entry.Id.ToString("D"));
        command.Parameters.AddWithValue("$extractor", entry.Extractor);
        command.Parameters.AddWithValue("$mediaId", entry.MediaId);
        command.Parameters.AddWithValue("$title", entry.Title);
        command.Parameters.AddWithValue("$sourceUrl", RedactUrl(entry.SourceUrl).AbsoluteUri);
        command.Parameters.AddWithValue("$format", entry.Format.ToString());
        command.Parameters.AddWithValue("$quality", entry.Quality.ToString());
        command.Parameters.AddWithValue("$outputPath", entry.OutputPath);
        command.Parameters.AddWithValue("$state", entry.State.ToString());
        command.Parameters.AddWithValue("$errorCategory", (object?)entry.ErrorCategory ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", entry.CreatedAt.ToString("O"));
        // The thumbnail URL is the publisher's own CDN address and carries no user data, so it is
        // stored as-is rather than going through RedactUrl, which would strip the signing query.
        command.Parameters.AddWithValue("$thumbnailUrl", (object?)entry.ThumbnailUrl?.AbsoluteUri ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<HistoryEntry>> SearchAsync(string? query, DownloadJobState? state, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, extractor, media_id, title, source_url, format, quality, output_path, state, error_category, created_at, thumbnail_url
            FROM history
            WHERE ($query = '' OR title LIKE $like ESCAPE '\' OR media_id LIKE $like ESCAPE '\')
              AND ($state IS NULL OR state = $state)
            ORDER BY created_at DESC;
            """;
        var search = query?.Trim() ?? string.Empty;
        command.Parameters.AddWithValue("$query", search);
        command.Parameters.AddWithValue("$like", $"%{EscapeLike(search)}%");
        command.Parameters.AddWithValue("$state", state?.ToString() ?? (object)DBNull.Value);

        var entries = new List<HistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new HistoryEntry(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3), new Uri(reader.GetString(4)),
                Enum.Parse<DownloadFormat>(reader.GetString(5)), Enum.Parse<VideoQuality>(reader.GetString(6)), reader.GetString(7),
                Enum.Parse<DownloadJobState>(reader.GetString(8)), reader.IsDBNull(9) ? null : reader.GetString(9), DateTimeOffset.Parse(reader.GetString(10)),
                reader.IsDBNull(11) || !Uri.TryCreate(reader.GetString(11), UriKind.Absolute, out var thumbnailUrl) ? null : thumbnailUrl));
        }

        return entries;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM history;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("歷史資料庫路徑無效。");
        Directory.CreateDirectory(directory);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS history (
                id TEXT PRIMARY KEY,
                extractor TEXT NOT NULL,
                media_id TEXT NOT NULL,
                title TEXT NOT NULL,
                source_url TEXT NOT NULL,
                format TEXT NOT NULL,
                quality TEXT NOT NULL,
                output_path TEXT NOT NULL,
                state TEXT NOT NULL,
                error_category TEXT NULL,
                created_at TEXT NOT NULL,
                thumbnail_url TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_history_created_at ON history(created_at DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await AddMissingColumnAsync(connection, "thumbnail_url", cancellationToken);
    }

    /// <summary>
    /// Brings a database written by an older build up to date. SQLite has no "ADD COLUMN IF NOT
    /// EXISTS", so the column list is read first.
    /// </summary>
    private static async Task AddMissingColumnAsync(SqliteConnection connection, string column, CancellationToken cancellationToken)
    {
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "SELECT COUNT(*) FROM pragma_table_info('history') WHERE name = $column;";
        inspect.Parameters.AddWithValue("$column", column);
        if (Convert.ToInt64(await inspect.ExecuteScalarAsync(cancellationToken)) > 0)
        {
            return;
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE history ADD COLUMN {column} TEXT NULL;";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Uri RedactUrl(Uri source) => new UriBuilder(source) { Query = string.Empty, Fragment = string.Empty }.Uri;

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}
