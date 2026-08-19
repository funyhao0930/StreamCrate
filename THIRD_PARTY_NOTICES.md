# Third-party notices

StreamCrate 的原始碼採 MIT License；應用程式可能依使用情境下載或包含下列第三方軟體。它們不是 StreamCrate 的授權條款，且各自保有完整的著作權與授權。

| 元件 | 用途 | 授權與來源 |
| --- | --- | --- |
| yt-dlp nightly | 媒體資訊解析與下載 | [Unlicense](https://github.com/yt-dlp/yt-dlp/blob/master/LICENSE)；[官方 Nightly Builds](https://github.com/yt-dlp/yt-dlp-nightly-builds/releases) |
| Deno | 執行 yt-dlp 的 YouTube JavaScript challenge solver | [MIT License](https://github.com/denoland/deno/blob/main/LICENSE.md)；[官方 Releases](https://github.com/denoland/deno/releases) |
| FFmpeg（win64 LGPL static build） | 音訊／視訊合併與轉檔 | [LGPL v2.1 or later](https://ffmpeg.org/legal.html)；[BtbN builds](https://github.com/BtbN/FFmpeg-Builds) |
| Windows App SDK / WinUI 3 | Windows 桌面介面 | [MIT License](https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE) |
| CommunityToolkit.Mvvm | MVVM 支援 | [MIT License](https://github.com/CommunityToolkit/dotnet/blob/main/License.md) |
| Microsoft.Data.Sqlite | 本機 SQLite 資料存取 | [MIT License](https://github.com/dotnet/efcore/blob/main/LICENSE.txt) |
| xUnit | 測試 | [Apache-2.0](https://github.com/xunit/xunit/blob/main/LICENSE) |

## FFmpeg 與 GPL 元件

StreamCrate 只選擇 BtbN Release 中的 `win64-lgpl` static 資產，排除 `master`、`shared`、`gpl` 與 `nonfree` 資產。實際下載的版本、授權與相依元件清單由該版本的 FFmpeg 發布物決定；發行者必須保留並隨散布物提供相應的授權文字與原始碼取得資訊。

## 取得原始碼

第三方元件的原始碼、授權文本與更新版本，請由上表連結取得。StreamCrate 不修改 yt-dlp、Deno 或 FFmpeg 的原始碼。
