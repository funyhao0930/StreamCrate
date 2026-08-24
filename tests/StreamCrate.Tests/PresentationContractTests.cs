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

    [Theory]
    [InlineData(DownloadJobState.Queued, "InProgress")]
    [InlineData(DownloadJobState.Probing, "InProgress")]
    [InlineData(DownloadJobState.Downloading, "InProgress")]
    [InlineData(DownloadJobState.PostProcessing, "InProgress")]
    [InlineData(DownloadJobState.Cancelled, "InProgress")]
    [InlineData(DownloadJobState.SkippedExisting, "InProgress")]
    [InlineData(DownloadJobState.Completed, "Completed")]
    [InlineData(DownloadJobState.Failed, "Failed")]
    public void Queue_item_places_each_download_state_in_its_expected_section(DownloadJobState state, string expectedSection)
    {
        var job = CreateJob(state);

        var item = CreateQueueItem(job);

        Assert.Equal(expectedSection, GetQueueItemProperty<string>(item, "Section"));
    }

    [Fact]
    public void Completed_queue_item_exposes_its_output_folder_for_open_location_action()
    {
        var job = CreateJob(DownloadJobState.Completed);

        var item = CreateQueueItem(job);

        Assert.Equal(@"D:\Media", GetQueueItemProperty<string>(item, "OutputPath"));
    }

    [Fact]
    public void Failed_queue_item_exposes_a_visible_safe_failure_message()
    {
        const string rawFailure = "ERROR: DRM protected content, token=private-value";
        var job = CreateJob(DownloadJobState.Failed, rawFailure);

        var item = CreateQueueItem(job);

        var message = GetQueueItemProperty<string>(item, "ErrorMessage");
        Assert.Contains("DRM", message);
        Assert.DoesNotContain(rawFailure, message);
        Assert.DoesNotContain("private-value", message);
    }

    [Fact]
    public void Queue_view_defines_separate_status_sections_with_motion_and_status_actions()
    {
        var xaml = File.ReadAllText(FindRepositoryFile("src", "StreamCrate.App", "MainWindow.xaml"));

        Assert.Contains("x:Name=\"InProgressQueueSection\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CompletedQueueSection\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FailedQueueSection\"", xaml, StringComparison.Ordinal);
        Assert.Contains("RepositionThemeTransition", xaml, StringComparison.Ordinal);
        Assert.Contains("開啟檔案位置", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"{Binding OutputPath}\" Click=\"OpenQueueFolderClicked\"", xaml, StringComparison.Ordinal);
        Assert.Contains("✖", xaml, StringComparison.Ordinal);
        Assert.Contains("#33FF4D4F", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Queue_section_synchronizer_preserves_instances_and_reorders_to_snapshot_order()
    {
        var first = CreateQueueItem(CreateJob(DownloadJobState.Queued));
        var second = CreateQueueItem(CreateJob(DownloadJobState.Completed));
        var queueItemType = first.GetType();
        var collectionType = typeof(System.Collections.ObjectModel.ObservableCollection<>).MakeGenericType(queueItemType);
        var items = Assert.IsAssignableFrom<System.Collections.IList>(Activator.CreateInstance(collectionType));
        items.Add(first);
        items.Add(second);
        var expectedItems = Array.CreateInstance(queueItemType, 2);
        expectedItems.SetValue(second, 0);
        expectedItems.SetValue(first, 1);

        var synchronizerType = typeof(MainWindow).Assembly.GetType("StreamCrate.App.Presentation.QueueSectionSynchronizer");
        Assert.NotNull(synchronizerType);
        var synchronize = synchronizerType.GetMethod("Synchronize", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(synchronize);
        synchronize.Invoke(null, [items, expectedItems]);

        Assert.Same(second, items[0]);
        Assert.Same(first, items[1]);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(3, 0, true)]
    [InlineData(3, 2, true)]
    [InlineData(3, 3, false)]
    public void Page_entrance_waits_for_layout_until_all_item_containers_are_realized(
        int itemCount,
        int realizedContainerCount,
        bool expected)
    {
        var type = typeof(MainWindow).Assembly.GetType("StreamCrate.App.Presentation.PageEntranceAnimationScheduler");
        Assert.NotNull(type);
        var method = type.GetMethod("RequiresLayoutPass", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var requiresLayoutPass = Assert.IsType<bool>(method.Invoke(null, [itemCount, realizedContainerCount]));

        Assert.Equal(expected, requiresLayoutPass);
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

    private static DownloadJob CreateJob(DownloadJobState state, string? failureMessage = null)
    {
        var request = new DownloadRequest(
            new MediaItem("test", "queue-item", "Queue item", new Uri("https://example.test/queue-item"), null),
            @"D:\Media",
            DownloadFormat.Mp4,
            VideoQuality.Best,
            CookieSelection.None);
        var job = new DownloadJob(request);
        if (state == DownloadJobState.Failed)
        {
            job.SetFailure(failureMessage ?? "下載工具錯誤", "下載工具錯誤");
        }
        else
        {
            job.SetState(state);
        }

        return job;
    }

    private static object CreateQueueItem(DownloadJob job)
    {
        var type = typeof(MainWindow).Assembly.GetType("StreamCrate.App.Presentation.QueueItem");
        Assert.NotNull(type);
        var item = Activator.CreateInstance(type, job);
        Assert.NotNull(item);
        return item;
    }

    private static T GetQueueItemProperty<T>(object item, string propertyName)
    {
        var property = item.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<T>(property.GetValue(item));
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("找不到工作區檔案。", Path.Combine(segments));
    }
}
