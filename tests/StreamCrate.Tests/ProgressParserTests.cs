using StreamCrate.Core.Models;
using StreamCrate.Infrastructure.Processes;

namespace StreamCrate.Tests;

public sealed class ProgressParserTests
{
    [Fact]
    public void Parses_machine_readable_download_progress()
    {
        var progress = YtDlpProgressParser.TryParse("SCPROGRESS| 62.5%|12.4MiB/s|1.2GiB|01:18");

        Assert.NotNull(progress);
        Assert.Equal(62.5, progress.Percent);
        Assert.Equal("12.4MiB/s", progress.Speed);
        Assert.Equal("1.2GiB", progress.TotalSize);
        Assert.Equal(TimeSpan.FromSeconds(78), progress.Eta);
        Assert.Equal(DownloadJobState.Downloading, progress.State);
    }

    [Fact]
    public void Reads_a_bare_mm_ss_eta_as_minutes_and_seconds()
    {
        var progress = YtDlpProgressParser.TryParse("SCPROGRESS|  5.2%|69.08KiB/s|3.57MiB|00:50");

        Assert.Equal(TimeSpan.FromSeconds(50), progress!.Eta);
    }

    [Fact]
    public void Reads_an_hour_long_eta()
    {
        var progress = YtDlpProgressParser.TryParse("SCPROGRESS|  1.0%|69.08KiB/s|3.57GiB|01:02:03");

        Assert.Equal(new TimeSpan(1, 2, 3), progress!.Eta);
    }

    [Fact]
    public void Drops_the_placeholder_values_yt_dlp_prints_before_it_has_figures()
    {
        var progress = YtDlpProgressParser.TryParse("SCPROGRESS|  0.0%| Unknown B/s|   3.57MiB|Unknown");

        Assert.NotNull(progress);
        Assert.Equal(0, progress.Percent);
        Assert.Equal(string.Empty, progress.Speed);
        Assert.Equal("3.57MiB", progress.TotalSize);
        Assert.Null(progress.Eta);
    }

    [Theory]
    [InlineData("[download] Destination: probeout\t.mp4")]
    [InlineData("[generic] big: Downloading webpage")]
    [InlineData("  0.0%| Unknown B/s|   3.57MiB|Unknown")]
    public void Ignores_lines_that_are_not_progress(string line) =>
        Assert.Null(YtDlpProgressParser.TryParse(line));
}
