using System.Collections;
using System.Reflection;
using StreamCrate.App;
using StreamCrate.Core.Models;

namespace StreamCrate.Tests;

public sealed class TimelineAndRateTests
{
    [Theory]
    [InlineData(null, 0d)]
    [InlineData("", 0d)]
    [InlineData("Unknown", 0d)]
    [InlineData("1.00MiB/s", 1d)]
    [InlineData("2.5MB/s", 2.5d)]
    [InlineData("1024KiB/s", 1d)]
    [InlineData("1.0GiB/s", 1024d)]
    public void Transfer_rate_is_read_as_megabytes_per_second(string? speed, double expected)
    {
        var actual = InvokeRate(speed);

        Assert.Equal(expected, actual, 3);
    }

    [Fact]
    public void Sparkline_needs_at_least_two_samples_and_a_drawable_box()
    {
        Assert.Empty(InvokeSparkline([1d], 100, 40));
        Assert.Empty(InvokeSparkline([1d, 2d], 0, 40));
        Assert.Equal(3, InvokeSparkline([1d, 2d, 3d], 100, 40).Count);
    }

    [Fact]
    public void Sparkline_pins_the_peak_sample_to_the_top_and_spans_the_full_width()
    {
        var points = InvokeSparkline([0d, 5d, 2.5d], 100, 40);

        Assert.Equal(0d, XOf(At(points, 0)), 3);
        Assert.Equal(40d, YOf(At(points, 0)), 3);
        Assert.Equal(0d, YOf(At(points, 1)), 3);
        Assert.Equal(100d, XOf(At(points, 2)), 3);
        Assert.Equal(20d, YOf(At(points, 2)), 3);
    }

    [Fact]
    public void History_groups_by_local_day_newest_first_and_labels_today()
    {
        var today = new DateTime(2026, 9, 13);
        var entries = new[]
        {
            CreateEntry("older", new DateTime(2026, 9, 11, 8, 0, 0), DownloadJobState.Completed),
            CreateEntry("today-early", new DateTime(2026, 9, 13, 9, 0, 0), DownloadJobState.Failed),
            CreateEntry("today-late", new DateTime(2026, 9, 13, 18, 0, 0), DownloadJobState.Completed),
        };

        var groups = InvokeBuild(entries, today);

        Assert.Equal(2, groups.Count);
        Assert.Equal("今天", GetProperty<string>(At(groups, 0), "Label"));
        Assert.True(GetProperty<bool>(At(groups, 0), "IsToday"));
        Assert.Equal("2026/09/11", GetProperty<string>(At(groups, 1), "Label"));
        Assert.False(GetProperty<bool>(At(groups, 1), "IsToday"));
        Assert.Equal(2, GetProperty<IEnumerable>(At(groups, 0), "Items").Cast<object>().Count());
    }

    [Fact]
    public void History_rows_indent_progressively_and_flag_failures()
    {
        var today = new DateTime(2026, 9, 13);
        var entries = new[]
        {
            CreateEntry("first", new DateTime(2026, 9, 13, 18, 0, 0), DownloadJobState.Completed),
            CreateEntry("second", new DateTime(2026, 9, 13, 9, 0, 0), DownloadJobState.Failed),
        };

        var rows = GetProperty<IEnumerable>(At(InvokeBuild(entries, today), 0), "Items").Cast<object>().ToArray();

        Assert.False(GetProperty<bool>(rows[0], "IsFailed"));
        Assert.True(GetProperty<bool>(rows[1], "IsFailed"));
        Assert.True(LeftOf(rows[1]) > LeftOf(rows[0]));
    }

    private static HistoryEntry CreateEntry(string title, DateTime localCreatedAt, DownloadJobState state) => new(
        Guid.NewGuid(),
        "youtube",
        title,
        title,
        new Uri($"https://example.test/{title}"),
        DownloadFormat.Mp4,
        VideoQuality.Best,
        @"D:\Media",
        state,
        null,
        new DateTimeOffset(localCreatedAt, TimeZoneInfo.Local.GetUtcOffset(localCreatedAt)));

    private static Type TransferRateType => typeof(MainWindow).Assembly.GetType("StreamCrate.App.Presentation.TransferRate")!;

    private static Type DayGroupType => typeof(MainWindow).Assembly.GetType("StreamCrate.App.Presentation.HistoryDayGroup")!;

    private static double InvokeRate(string? speed) =>
        (double)TransferRateType.GetMethod("ToMegabytesPerSecond")!.Invoke(null, [speed])!;

    private static IList InvokeSparkline(double[] samples, double width, double height) =>
        (IList)TransferRateType.GetMethod("BuildSparkline")!.Invoke(null, [samples, width, height])!;

    private static IList InvokeBuild(HistoryEntry[] entries, DateTime today) =>
        (IList)DayGroupType.GetMethod("Build")!.Invoke(null, [entries, today])!;

    private static object At(IList list, int index) => list[index]!;

    private static T GetProperty<T>(object instance, string name) =>
        (T)instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(instance)!;

    private static double LeftOf(object row) =>
        (double)GetProperty<object>(row, "Indent").GetType().GetProperty("Left")!.GetValue(GetProperty<object>(row, "Indent"))!;

    private static double XOf(object point) => (double)point.GetType().GetField("Item1")!.GetValue(point)!;

    private static double YOf(object point) => (double)point.GetType().GetField("Item2")!.GetValue(point)!;
}
