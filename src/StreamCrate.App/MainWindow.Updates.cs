using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StreamCrate.Core.Models;
using StreamCrate.Infrastructure.Updates;

namespace StreamCrate.App;

public sealed partial class MainWindow
{
    private readonly AppUpdateService _updater = new(new HttpClient());
    private AppUpdateInfo? _availableUpdate;
    private bool _isCheckingUpdate;
    private bool _isInstallingUpdate;

    private static Version CurrentVersion
    {
        get
        {
            var informational = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (informational is not null && AppUpdateService.TryParseVersion(informational, out var version))
            {
                return version;
            }

            return typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0);
        }
    }

    private static string CurrentVersionLabel => CurrentVersion.ToString(3);

    // Called on every launch. A failed background check stays quiet: being offline or hitting
    // GitHub's rate limit is no reason to put an error in front of someone who came to download.
    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (_isCheckingUpdate || _isInstallingUpdate)
        {
            return;
        }

        _isCheckingUpdate = true;
        CurrentVersionText.Text = $"目前版本 {CurrentVersionLabel}";
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在檢查更新…";
        try
        {
            _availableUpdate = await _updater.CheckForUpdateAsync(CurrentVersion);
            if (_availableUpdate is { } update)
            {
                var label = update.Version.ToString(3);
                UpdateStatusText.Text = $"有新版本 {label} 可以安裝。";
                InstallUpdateButton.Visibility = Visibility.Visible;
                ReleaseNotesLink.NavigateUri = update.ReleasePageUrl;
                ReleaseNotesLink.Visibility = Visibility.Visible;
                UpdateBar.Title = $"StreamCrate {label} 已推出";
                UpdateBar.Message = $"目前版本 {CurrentVersionLabel}。更新會下載安裝檔、驗證後自動安裝並重新開啟。";
                UpdateBar.IsOpen = true;
            }
            else
            {
                UpdateStatusText.Text = $"已是最新版本（{DateTime.Now:HH:mm} 檢查）。";
                InstallUpdateButton.Visibility = Visibility.Collapsed;
                ReleaseNotesLink.Visibility = Visibility.Collapsed;
                UpdateBar.IsOpen = false;
            }
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text = "無法檢查更新，請確認網路連線後再試一次。";
            if (userInitiated)
            {
                ShowError("檢查更新失敗", exception.Message);
            }
        }
        finally
        {
            _isCheckingUpdate = false;
            CheckUpdateButton.IsEnabled = !_isInstallingUpdate;
        }
    }

    private async void CheckUpdateClicked(object sender, RoutedEventArgs args) => await CheckForUpdatesAsync(userInitiated: true);

    private async void InstallUpdateClicked(object sender, RoutedEventArgs args)
    {
        if (_availableUpdate is not { } update || _isInstallingUpdate)
        {
            return;
        }

        if (HasActiveDownloads() && !await ConfirmInstallDuringDownloadsAsync())
        {
            return;
        }

        _isInstallingUpdate = true;
        UpdateBar.IsOpen = false;
        CheckUpdateButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateBarInstallButton.IsEnabled = false;
        UpdateProgressBar.Value = 0;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateStatusText.Text = $"正在下載 {update.Version.ToString(3)}…";
        try
        {
            var progress = new Progress<double>(value =>
            {
                UpdateProgressBar.Value = value;
                UpdateStatusText.Text = $"正在下載 {update.Version.ToString(3)}… {value:P0}";
            });
            var installerPath = await _updater.DownloadInstallerAsync(update, progress);
            UpdateStatusText.Text = "下載完成，正在啟動安裝程式…";
            AppUpdateService.LaunchInstaller(installerPath);
            Application.Current.Exit();
        }
        catch (Exception exception)
        {
            _isInstallingUpdate = false;
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            CheckUpdateButton.IsEnabled = true;
            InstallUpdateButton.IsEnabled = true;
            UpdateBarInstallButton.IsEnabled = true;
            UpdateStatusText.Text = "更新失敗，可以稍後再試，或到版本說明頁手動下載。";
            ShowError("更新失敗", exception.Message);
        }
    }

    private bool HasActiveDownloads() => _queue.GetSnapshot().Any(job => job.State is
        DownloadJobState.Queued or
        DownloadJobState.Probing or
        DownloadJobState.Downloading or
        DownloadJobState.PostProcessing);

    private async Task<bool> ConfirmInstallDuringDownloadsAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "仍有下載進行中",
            Content = "安裝更新會關閉 StreamCrate，佇列中尚未完成的下載將被中斷。要繼續嗎？",
            PrimaryButtonText = "仍要更新",
            CloseButtonText = "稍後再說",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
