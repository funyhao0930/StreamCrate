using System.Text;
using System.Text.Json.Nodes;
using StreamCrate.Core.Models;
using StreamCrate.Infrastructure.Processes;

namespace StreamCrate.Tests;

public sealed class BilibiliDownloadAcceleratorTests
{
    [Theory]
    [InlineData(1L)]
    [InlineData(4L * 1024 * 1024)]
    [InlineData(4L * 1024 * 1024 + 1)]
    [InlineData(253_708_968L)]
    public void Playlist_byte_ranges_cover_the_whole_file_exactly_once(long size)
    {
        var playlist = BilibiliDownloadAccelerator.BuildPlaylist("https://upos.example/v.m4s?sig=1", size);

        long expectedStart = 0;
        foreach (var line in playlist.Split('\n').Where(line => line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal)))
        {
            var parts = line["#EXT-X-BYTERANGE:".Length..].Split('@');
            var length = long.Parse(parts[0]);
            var start = long.Parse(parts[1]);
            Assert.Equal(expectedStart, start);
            Assert.InRange(length, 1, BilibiliDownloadAccelerator.FragmentSize);
            expectedStart = start + length;
        }

        Assert.Equal(size, expectedStart);
        Assert.EndsWith("#EXT-X-ENDLIST\n", playlist);
    }

    [Fact]
    public void Candidates_start_with_the_original_and_never_rewrite_to_akamai()
    {
        const string original = "https://upos-hz-mirrorakam.akamaized.net/upgcxcode/1/2/3-1-30077.m4s?e=abc&upsig=x";

        var candidates = BilibiliDownloadAccelerator.CandidateUrls(original);

        Assert.Equal(original, candidates[0]);
        Assert.All(candidates.Skip(1), url => Assert.DoesNotContain("akamaized", url));
        Assert.All(candidates.Skip(1), url => Assert.EndsWith("/upgcxcode/1/2/3-1-30077.m4s?e=abc&upsig=x", url));
        Assert.Equal(1 + BilibiliDownloadAccelerator.MirrorHosts.Count, candidates.Count);
    }

    [Fact]
    public void Candidates_do_not_repeat_the_original_mirror()
    {
        const string original = "https://upos-sz-mirrorcosov.bilivideo.com/v.m4s?x=1";

        var candidates = BilibiliDownloadAccelerator.CandidateUrls(original);

        Assert.Equal(candidates.Count, candidates.Distinct().Count());
        Assert.Equal(BilibiliDownloadAccelerator.MirrorHosts.Count, candidates.Count);
    }

    [Fact]
    public void Rewrite_turns_chosen_formats_into_byte_range_playlists_and_selects_them()
    {
        var info = JsonNode.Parse("""
            {"id":"BV1","formats":[
              {"format_id":"30077","url":"https://a/v","protocol":"https"},
              {"format_id":"30280","url":"https://a/a","protocol":"https"},
              {"format_id":"30016","url":"https://a/low","protocol":"https"}],
             "requested_formats":[
              {"format_id":"30077","url":"https://a/v","protocol":"https","http_headers":{"Referer":"https://www.bilibili.com/"}},
              {"format_id":"30280","url":"https://a/a","protocol":"https"}]}
            """)!.AsObject();
        var streams = BilibiliDownloadAccelerator.GetRequestedStreams(info);
        Assert.Equal(["30077", "30280"], streams.Select(stream => stream.FormatId));
        Assert.Equal("https://www.bilibili.com/", streams[0].Headers["Referer"]);

        var formatIds = BilibiliDownloadAccelerator.Rewrite(info, new Dictionary<string, BilibiliDownloadAccelerator.ChosenStream>
        {
            ["30077"] = new("https://mirror/v", 10),
            ["30280"] = new("https://mirror/a", 5),
        });

        Assert.Equal("30077+30280", formatIds);
        Assert.Null(info["requested_formats"]);
        var video = info["formats"]![0]!.AsObject();
        Assert.Equal("m3u8_native", video["protocol"]!.GetValue<string>());
        Assert.Equal(10, video["filesize"]!.GetValue<long>());
        var playlist = Encoding.UTF8.GetString(Convert.FromBase64String(
            video["url"]!.GetValue<string>()["data:application/vnd.apple.mpegurl;base64,".Length..]));
        Assert.Contains("https://mirror/v", playlist);
        Assert.Equal("https", info["formats"]![2]!["protocol"]!.GetValue<string>());
    }

    [Fact]
    public void Streams_that_are_not_plain_http_are_left_alone()
    {
        var info = JsonNode.Parse("""
            {"format_id":"hls","url":"https://a/master.m3u8","protocol":"m3u8_native"}
            """)!.AsObject();

        Assert.Empty(BilibiliDownloadAccelerator.GetRequestedStreams(info));
    }

    [Theory]
    [InlineData("https://www.bilibili.com/video/BV1", DownloadFormat.Mp4, true)]
    [InlineData("https://www.bilibili.com/video/BV1", DownloadFormat.Mp3, false)]
    [InlineData("https://www.youtube.com/watch?v=1", DownloadFormat.Mp4, false)]
    public void Applies_only_to_bilibili_video(string url, DownloadFormat format, bool expected)
    {
        var request = new DownloadRequest(
            new MediaItem("site", "1", "t", new Uri(url), null),
            @"C:\Downloads", format, VideoQuality.Best, CookieSelection.None, null, null);

        Assert.Equal(expected, BilibiliDownloadAccelerator.Applies(request));
    }

    [Fact]
    public void Accelerated_download_loads_the_prepared_info_json_with_parallel_fragments()
    {
        var request = DownloadRequest.CreateVideo(
            new MediaItem("BiliBili", "BV1", "t", new Uri("https://www.bilibili.com/video/BV1"), null),
            @"C:\Downloads",
            VideoQuality.Best);

        var specification = new YtDlpCommandFactory().CreateDownload(
            request, @"C:\tools", new AcceleratedSource(@"C:\Temp\x.info.json", "30077+30280"));

        var arguments = specification.Arguments.ToList();
        Assert.Equal(@"C:\Temp\x.info.json", arguments[arguments.IndexOf("--load-info-json") + 1]);
        Assert.Equal("30077+30280", arguments[arguments.IndexOf("--format") + 1]);
        Assert.Contains("--concurrent-fragments", arguments);
        Assert.DoesNotContain("https://www.bilibili.com/video/BV1", arguments);
        Assert.DoesNotContain("--http-chunk-size", arguments);
    }
}
