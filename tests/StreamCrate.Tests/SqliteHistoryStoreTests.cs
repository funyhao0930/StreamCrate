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
