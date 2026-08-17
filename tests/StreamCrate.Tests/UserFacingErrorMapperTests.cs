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
}
