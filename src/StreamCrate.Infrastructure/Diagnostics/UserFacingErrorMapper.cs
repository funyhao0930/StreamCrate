namespace StreamCrate.Infrastructure.Diagnostics;

public static class UserFacingErrorMapper
{
    public static string Map(string message)
    {
        if (message.Contains("DRM", StringComparison.OrdinalIgnoreCase))
        {
            return "此內容受到 DRM 保護，StreamCrate 不支援下載或繞過 DRM。請使用平台提供的官方觀看或離線功能。";
        }

        if (message.Contains("Failed to decrypt with DPAPI", StringComparison.OrdinalIgnoreCase))
        {
            return "無法解密 Edge 或 Chrome 的 Cookie。請改選 cookies.txt，選取僅供本次使用的 Netscape cookies.txt 檔案。";
        }

        if (message.Contains("Could not copy Chrome cookie database", StringComparison.OrdinalIgnoreCase))
        {
            return "無法讀取 Edge 或 Chrome 的 Cookie。請完全關閉瀏覽器，並在工作管理員結束其背景程序後再重試；若仍失敗，請改用 cookies.txt。";
        }

        return SensitiveDataSanitizer.Sanitize(message);
    }
}
