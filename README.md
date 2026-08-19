# StreamCrate

StreamCrate 是一個為 Windows 10 22H2 與 Windows 11 x64 設計的本機優先影片下載器。它以 [yt-dlp](https://github.com/yt-dlp/yt-dlp) 與 FFmpeg 處理使用者有權下載的公開或已登入媒體，提供 MP4、MP3、播放清單、FIFO 佇列、可續傳下載與本機歷史紀錄。

> 請只下載你有權存取與保存的內容。StreamCrate 不提供 DRM 規避功能，也不保證所有網站永遠可用。

## 功能

- 左側導覽：新增下載、下載佇列、歷史紀錄、設定
- 單一影片與播放清單解析；播放清單可個別取消勾選
- MP4（最佳、2160p、1440p、1080p、720p）與高品質 MP3
- 一次一項的 FIFO 下載、取消、失敗重試與 `.part` 續傳
- Chrome、Edge、Firefox Cookie 匯入，或僅限本次使用的 Netscape `cookies.txt`
- 繁體中文／English、淺色／深色主題
- 無遙測；Cookie、授權標頭與可能含 token 的 URL 查詢字串不會寫入歷史或日誌

## 安裝與使用

從 GitHub Releases 下載 x64 安裝程式後執行。首次啟動時，程式會說明並要求同意下載 yt-dlp 與 LGPL 版 FFmpeg；工具會下載到 `%LocalAppData%\StreamCrate`，媒體檔案則儲存在你選擇的資料夾。

未簽章的第一版可能觸發 Windows SmartScreen。請只從本專案的 Release 下載，並用 Release 同附的 SHA-256 檔案核對安裝程式：

```powershell
Get-FileHash .\StreamCrate-Setup-x64.exe -Algorithm SHA256
```

## 開發

需求：Windows、.NET SDK 10、Visual Studio Build Tools（含 Windows App SDK/WinUI 工作負載）及 Inno Setup 6（僅建立安裝程式時需要）。

```powershell
dotnet restore StreamCrate.sln --locked-mode
dotnet build StreamCrate.sln -c Release -p:Platform=x64 --no-restore
dotnet test tests/StreamCrate.Tests/StreamCrate.Tests.csproj -c Release --no-build
dotnet publish src/StreamCrate.App/StreamCrate.App.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o artifacts/publish
```

以 Inno Setup 編譯 `installer/StreamCrate.iss`，它預期 publish 輸出位於 `artifacts/publish`。

## 隱私與安全

StreamCrate 不含遙測。網路連線僅用於你要求的媒體操作、工具更新與 GitHub Release 更新檢查。完整資料處理說明在 [PRIVACY.md](PRIVACY.md)。使用第三方元件前，請閱讀 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 授權

本專案以 [MIT License](LICENSE) 授權。yt-dlp、FFmpeg 與其相依元件各自依其原始授權條款提供。
