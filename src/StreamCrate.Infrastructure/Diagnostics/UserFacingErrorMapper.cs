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
            return "Windows 的 App-Bound Encryption 阻止 StreamCrate 直接解密 Edge 或 Chrome Cookie。公開內容請選「不使用 Cookie」；需要登入時請改用 Firefox，或選擇從瀏覽器匯出的 Netscape cookies.txt（僅供本次使用）。";
        }

        if (message.Contains("Could not copy Chrome cookie database", StringComparison.OrdinalIgnoreCase))
        {
            return "無法讀取 Edge 或 Chrome 的 Cookie。請完全關閉瀏覽器，並在工作管理員結束其背景程序後再重試；若仍失敗，請改用 cookies.txt。";
        }

        if (message.Contains("unable to download video data", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("HTTP Error 403: Forbidden", StringComparison.OrdinalIgnoreCase))
        {
            return "YouTube 拒絕影音串流請求（HTTP 403）。StreamCrate 已使用 yt-dlp nightly、Deno 並改用 IPv4 重試一次；公開影片請重新解析後再試，只有需要登入的內容才改用 Firefox 或 cookies.txt。";
        }

        return SensitiveDataSanitizer.Sanitize(message);
    }
}
