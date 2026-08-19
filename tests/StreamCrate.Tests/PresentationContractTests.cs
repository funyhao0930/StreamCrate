using System.Reflection;
using StreamCrate.App;
using StreamCrate.Core.Models;
using StreamCrate.Infrastructure.Processes;

namespace StreamCrate.Tests;

public sealed class PresentationContractTests
{
    [Fact]
    public void BuildPlaylistRequests_keeps_selected_items_in_source_order_with_playlist_metadata()
    {
        var first = new MediaItem("youtube", "a", "First", new Uri("https://example.test/a"), TimeSpan.FromMinutes(1));
        var second = new MediaItem("youtube", "b", "Second", new Uri("https://example.test/b"), TimeSpan.FromMinutes(2));
        var third = new MediaItem("youtube", "c", "Third", new Uri("https://example.test/c"), TimeSpan.FromMinutes(3));
        var requests = InvokePlaylistBuilder(
            [first, second, third],
            [true, false, true],
            "Summer list");

        Assert.Collection(
            requests,
            request =>
            {
                Assert.Equal("a", request.Media.MediaId);
                Assert.Equal("Summer list", request.PlaylistTitle);
                Assert.Equal(1, request.PlaylistIndex);
            },
            request =>
            {
                Assert.Equal("c", request.Media.MediaId);
                Assert.Equal("Summer list", request.PlaylistTitle);
                Assert.Equal(3, request.PlaylistIndex);
            });
    }

    [Fact]
    public void Playlist_selection_starts_with_every_item_selected_and_can_be_cleared()
    {
        var first = new MediaItem("youtube", "a", "First", new Uri("https://example.test/a"), null);
        var second = new MediaItem("youtube", "b", "Second", new Uri("https://example.test/b"), null);
        var playlist = new PlaylistInfo("summer", "Summer list", [first, second]);
        var type = typeof(MainWindow).Assembly.GetType("StreamCrate.App.Presentation.PlaylistSelection");
        Assert.NotNull(type);
        var selection = Activator.CreateInstance(type, playlist);
        Assert.NotNull(selection);

        var selectedCount = type.GetProperty("SelectedCount");
        var clear = type.GetMethod("SetAllSelected");
        Assert.NotNull(selectedCount);
        Assert.NotNull(clear);
        Assert.Equal(2, Assert.IsType<int>(selectedCount.GetValue(selection)));

        clear.Invoke(selection, [false]);

        Assert.Equal(0, Assert.IsType<int>(selectedCount.GetValue(selection)));
        var createRequests = type.GetMethod("CreateRequests");
        Assert.NotNull(createRequests);
        var requests = Assert.IsAssignableFrom<IReadOnlyList<DownloadRequest>>(
            createRequests.Invoke(selection, [@"D:\Media", DownloadFormat.Mp4, VideoQuality.Best, CookieSelection.None]));
        Assert.Empty(requests);
    }

    [Theory]
    [InlineData(DownloadJobState.Queued, "等待中")]
    [InlineData(DownloadJobState.Probing, "解析中")]
    [InlineData(DownloadJobState.Downloading, "下載中")]
    [InlineData(DownloadJobState.PostProcessing, "處理中")]
    [InlineData(DownloadJobState.Completed, "已完成")]
    [InlineData(DownloadJobState.Failed, "下載失敗")]
    [InlineData(DownloadJobState.Cancelled, "已取消")]
    [InlineData(DownloadJobState.SkippedExisting, "已略過（檔案已存在）")]
    [InlineData((DownloadJobState)999, "未知狀態")]
    public void Download_state_has_a_clear_traditional_chinese_label(DownloadJobState state, string expected)
    {
        var text = InvokeStateText(state);

        Assert.Equal(expected, text);
    }

    [Fact]
    public void YouTube_403_retry_policy_only_retries_stream_data_failures_and_forces_ipv4_once()
    {
        var type = typeof(MainWindow).Assembly.GetType("StreamCrate.App.Presentation.YouTube403RetryPolicy");
        Assert.NotNull(type);
        var shouldRetry = type.GetMethod("ShouldRetry", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        var withIpv4 = type.GetMethod("WithForceIpv4", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(shouldRetry);
        Assert.NotNull(withIpv4);

        const string stream403 = "ERROR: unable to download video data: HTTP Error 403: Forbidden";
        Assert.True(Assert.IsType<bool>(shouldRetry.Invoke(null, ["Youtube", stream403])));
        Assert.False(Assert.IsType<bool>(shouldRetry.Invoke(null, ["Vimeo", stream403])));
        Assert.False(Assert.IsType<bool>(shouldRetry.Invoke(null, ["Youtube", "ERROR: This video is DRM protected"])));

        var original = new ProcessSpecification("yt-dlp.exe", ["--newline", "https://example.test/watch?v=video"], ["--newline", "https://example.test/watch?v=video"]);
        var fallback = Assert.IsType<ProcessSpecification>(withIpv4.Invoke(null, [original]));
        Assert.Equal("--force-ipv4", fallback.Arguments[0]);
        Assert.Equal("--force-ipv4", fallback.RedactedDisplayArguments[0]);
        Assert.Single(fallback.Arguments, argument => argument == "--force-ipv4");
    }

    private static IReadOnlyList<DownloadRequest> InvokePlaylistBuilder(
        IReadOnlyList<MediaItem> items,
        IReadOnlyList<bool> selected,
        string playlistTitle)
    {
        var type = typeof(MainWindow).Assembly.GetType("StreamCrate.App.Presentation.PlaylistRequestBuilder");
        Assert.NotNull(type);
        var method = type.GetMethod("Build", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method.Invoke(null, [items, selected, playlistTitle, @"D:\Media", DownloadFormat.Mp4, VideoQuality.Best, CookieSelection.None]);
        return Assert.IsAssignableFrom<IReadOnlyList<DownloadRequest>>(result);
    }

    private static string InvokeStateText(DownloadJobState state)
    {
        var type = typeof(MainWindow).Assembly.GetType("StreamCrate.App.Presentation.DownloadStateText");
        Assert.NotNull(type);
        var method = type.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<string>(method.Invoke(null, [state]));
    }
}
