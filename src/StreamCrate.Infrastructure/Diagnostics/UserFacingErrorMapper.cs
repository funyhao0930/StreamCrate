namespace StreamCrate.Infrastructure.Diagnostics;

public static class UserFacingErrorMapper
{
    public static string Map(string message)
    {
        if (message.Contains("Could not copy Chrome cookie database", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Failed to decrypt with DPAPI", StringComparison.OrdinalIgnoreCase))
        {
            return "無法讀取 Edge 或 Chrome 的 Cookie。請完全關閉瀏覽器，並在工作管理員結束其背景程序後再重試。";
        }

        return SensitiveDataSanitizer.Sanitize(message);
    }
}
