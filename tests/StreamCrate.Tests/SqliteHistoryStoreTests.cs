using Microsoft.Data.Sqlite;
using StreamCrate.Core.Models;
using StreamCrate.Infrastructure.Storage;

namespace StreamCrate.Tests;

public sealed class SqliteHistoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"StreamCrate.Tests.{Guid.NewGuid():N}");

    [Fact]
    public async Task Save_redacts_query_string_and_search_returns_matching_entry()
    {
        var store = new SqliteHistoryStore(Path.Combine(_directory, "history.db"));
        var entry = new HistoryEntry(
            Guid.NewGuid(), "youtube", "abc", "Ocean documentary", new Uri("https://example.test/watch?v=abc&token=private"),
            DownloadFormat.Mp4, VideoQuality.P1080, @"D:\Media", DownloadJobState.Completed, null, DateTimeOffset.UtcNow);

        await store.SaveAsync(entry);

        var results = await store.SearchAsync("documentary", null);

        var saved = Assert.Single(results);
        Assert.Equal("https://example.test/watch", saved.SourceUrl.AbsoluteUri);
        Assert.DoesNotContain("token", saved.SourceUrl.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_keeps_the_thumbnail_url_intact_for_the_history_row()
    {
        var store = new SqliteHistoryStore(Path.Combine(_directory, "history.db"));
        var thumbnail = new Uri("https://img.example.test/abc/hq.jpg?sqp=signature&rs=token");

        await store.SaveAsync(new HistoryEntry(
            Guid.NewGuid(), "youtube", "abc", "Ocean documentary", new Uri("https://example.test/watch?v=abc"),
            DownloadFormat.Mp4, VideoQuality.P1080, @"D:\Media", DownloadJobState.Completed, null, DateTimeOffset.UtcNow, thumbnail));

        var saved = Assert.Single(await store.SearchAsync(null, null));
        // The signing query has to survive: the CDN rejects the request without it.
        Assert.Equal(thumbnail, saved.ThumbnailUrl);
    }

    [Fact]
    public async Task Search_reads_a_database_written_before_the_thumbnail_column_existed()
    {
        var path = Path.Combine(_directory, "history.db");
        Directory.CreateDirectory(_directory);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE history (
                    id TEXT PRIMARY KEY, extractor TEXT NOT NULL, media_id TEXT NOT NULL, title TEXT NOT NULL,
                    source_url TEXT NOT NULL, format TEXT NOT NULL, quality TEXT NOT NULL, output_path TEXT NOT NULL,
                    state TEXT NOT NULL, error_category TEXT NULL, created_at TEXT NOT NULL
                );
                INSERT INTO history VALUES ('11111111-1111-1111-1111-111111111111', 'youtube', 'abc', 'Old row',
                    'https://example.test/watch', 'Mp4', 'Best', 'D:\Media', 'Completed', NULL, '2026-01-01T00:00:00.0000000+00:00');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var saved = Assert.Single(await new SqliteHistoryStore(path).SearchAsync(null, null));

        Assert.Equal("Old row", saved.Title);
        Assert.Null(saved.ThumbnailUrl);
    }

    [Fact]
    public async Task Clear_removes_all_history_entries()
    {
        var store = new SqliteHistoryStore(Path.Combine(_directory, "history.db"));
        await store.SaveAsync(new HistoryEntry(
            Guid.NewGuid(), "youtube", "abc", "A video", new Uri("https://example.test/watch"),
            DownloadFormat.Mp4, VideoQuality.Best, @"D:\Media", DownloadJobState.Failed, "下載工具錯誤", DateTimeOffset.UtcNow));

        await store.ClearAsync();

        Assert.Empty(await store.SearchAsync(null, null));
    }

    public void Dispose()
    {
        foreach (var path in new[]
                 {
                     Path.Combine(_directory, "history.db"),
                     Path.Combine(_directory, "history.db-shm"),
                     Path.Combine(_directory, "history.db-wal"),
                 })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
