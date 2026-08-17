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
}
