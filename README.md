# StreamCrate

Windows x64 的本機優先影片下載器。貼上影片或播放清單網址，選擇格式、畫質與輸出資料夾，解析後加入下載佇列。

[下載最新版](https://github.com/funyhao0930/StreamCrate/releases/latest) · [隱私權說明](PRIVACY.md) · [第三方授權](THIRD_PARTY_NOTICES.md)

> 請只下載你有權存取與保存的內容。StreamCrate 不支援 DRM 規避，也不保證所有網站長期可用。

## 目前版本：V1.0.2

- 支援自訂背景圖片與頁面轉場效果。
- 下載佇列依等待中、下載中、已完成與失敗分區顯示。
- 下載路徑改用 Windows 資料夾選擇器。
- 更新 Windows x64 自包含安裝程式。

## 功能

- 單一影片與播放清單解析。
- 播放清單可逐項選取，依原始順序加入 FIFO 佇列。
- 支援 MP4：最佳、2160p、1440p、1080p、720p。
- 支援高品質 MP3。
- 支援取消、失敗重試與 `.part` 續傳。
- 播放清單輸出格式：

  ```text
  播放清單名稱\01 - 影片標題 [影片ID].mp4
  ```

- 繁體中文／English。
- 淺色／深色主題、自訂背景圖片與頁面轉場。
- 本機歷史紀錄。
- 不含遙測。

## 快速開始

1. 從 [GitHub Releases](https://github.com/funyhao0930/StreamCrate/releases/latest) 下載 x64 安裝程式。
2. 啟動 StreamCrate。首次啟動時，程式會要求同意下載 yt-dlp nightly、Deno 與 FFmpeg。
3. 貼上影片或播放清單網址，按下解析。
4. 選擇要下載的項目、格式、畫質、Cookie 來源與輸出資料夾。
5. 按下「加入下載佇列」，在佇列頁面查看進度。
6. 完成後可在歷史紀錄查看結果與輸出位置。

工具會從官方 Release 下載並驗證 SHA-256，保存於：

```text
%LocalAppData%\StreamCrate
```

媒體檔案則會儲存到你選擇的資料夾。

## Cookie 與網站限制

- 公開內容預設不使用 Cookie。
- 需要登入的內容可使用 Firefox，或選擇瀏覽器匯出的 Netscape `cookies.txt`。
- Windows 的 App-Bound Encryption 可能阻止直接讀取 Chrome／Edge Cookie。
- Cookie 只在本次操作期間使用，不會寫入設定、歷史紀錄或日誌。
- StreamCrate 不支援 DRM 規避或繞過平台存取控制。

若 YouTube 回傳 HTTP 403，請先重新解析網址；公開影片請維持「不使用 Cookie」，登入內容再改用 Firefox 或 `cookies.txt`。

## 安裝檔驗證

下載安裝程式後，可使用下列指令計算 SHA-256：

```powershell
Get-FileHash .\StreamCrate-Setup-x64.exe -Algorithm SHA256
```

請將結果與 Release 同附的 `.sha256` 檔案比對。未簽章的安裝程式可能觸發 Windows SmartScreen。

## 開發

需求：

- Windows 10 `10.0.19041` 以上或 Windows 11 x64
- .NET SDK 10
- Visual Studio Build Tools（含 Windows App SDK／WinUI 工作負載）
- Inno Setup 6（只有建立安裝程式時需要）

```powershell
dotnet restore StreamCrate.sln --locked-mode
dotnet build StreamCrate.sln -c Release -p:Platform=x64 --no-restore
dotnet test tests/StreamCrate.Tests/StreamCrate.Tests.csproj -c Release -p:Platform=x64 --no-build --no-restore
dotnet publish src/StreamCrate.App/StreamCrate.App.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o artifacts/publish
```

以 Inno Setup 編譯 `installer/StreamCrate.iss`；它預期 publish 輸出位於 `artifacts/publish`。

## 隱私與安全

StreamCrate 沒有遙測、帳號系統、廣告 SDK 或分析服務。網路連線只用於：

- 你要求的媒體解析與下載。
- 下載或更新 yt-dlp、Deno 與 FFmpeg。
- GitHub Release 更新檢查。

完整資料處理方式請參閱 [PRIVACY.md](PRIVACY.md)。

## 授權

本專案採用 [MIT License](LICENSE)。yt-dlp、Deno、FFmpeg 與其他相依元件各自依其原始授權條款提供。
