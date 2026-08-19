using StreamCrate.Infrastructure.Diagnostics;

namespace StreamCrate.Tests;

public sealed class UserFacingErrorMapperTests
{
    [Fact]
    public void Map_explains_browser_cookie_database_locks()
    {
        var message = UserFacingErrorMapper.Map("ERROR: Could not copy Chrome cookie database.");

        Assert.Contains("完全關閉", message);
        Assert.Contains("Edge 或 Chrome", message);
    }

    [Fact]
    public void Map_recommends_a_temporary_cookies_file_when_dpapi_decryption_fails()
    {
        var message = UserFacingErrorMapper.Map("ERROR: Failed to decrypt with DPAPI.");

        Assert.Contains("App-Bound Encryption", message);
        Assert.Contains("Firefox", message);
        Assert.Contains("cookies.txt", message);
        Assert.Contains("僅供本次使用", message);
    }

    [Fact]
    public void Map_explains_that_drm_protected_content_is_not_supported()
    {
        var message = UserFacingErrorMapper.Map("ERROR: This video is DRM protected");

        Assert.Contains("DRM", message);
        Assert.Contains("不支援", message);
    }

    [Fact]
    public void Map_explains_the_YouTube_403_stream_data_fallback()
    {
        var message = UserFacingErrorMapper.Map("ERROR: unable to download video data: HTTP Error 403: Forbidden");

        Assert.Contains("YouTube", message);
        Assert.Contains("nightly", message);
        Assert.Contains("Deno", message);
        Assert.Contains("IPv4", message);
        Assert.DoesNotContain("VPN", message);
    }
}
