using StreamCrate.Infrastructure.Processes;

namespace StreamCrate.Tests;

public sealed class ProgressParserTests
{
    [Fact]
    public void Parses_machine_readable_download_progress()
    {
        var progress = YtDlpProgressParser.TryParse("download: 62.5%|12.4MiB/s|1.2GiB|00:01:18");

        Assert.NotNull(progress);
        Assert.Equal(62.5, progress.Percent);
        Assert.Equal("12.4MiB/s", progress.Speed);
        Assert.Equal("1.2GiB", progress.TotalSize);
        Assert.Equal(TimeSpan.FromSeconds(78), progress.Eta);
    }
}
