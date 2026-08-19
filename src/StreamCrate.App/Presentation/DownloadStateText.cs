using StreamCrate.Core.Models;

namespace StreamCrate.App.Presentation;

internal static class DownloadStateText
{
    public static string Get(DownloadJobState state) => state switch
    {
        DownloadJobState.Queued => "等待中",
        DownloadJobState.Probing => "解析中",
        DownloadJobState.Downloading => "下載中",
        DownloadJobState.PostProcessing => "處理中",
        DownloadJobState.Completed => "已完成",
        DownloadJobState.Failed => "下載失敗",
        DownloadJobState.Cancelled => "已取消",
        DownloadJobState.SkippedExisting => "已略過（檔案已存在）",
        _ => "未知狀態",
    };
}
