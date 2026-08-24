using StreamCrate.Core.Models;
using StreamCrate.Infrastructure.Storage;

namespace StreamCrate.Tests;

public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"StreamCrate.Tests.{Guid.NewGuid():N}");

    [Fact]
    public async Task Save_then_load_preserves_download_and_theme_preferences()
    {
        var path = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(_directory);
        var expected = new AppSettings(_directory, DownloadFormat.Mp3, VideoQuality.P720, AppTheme.Light);

        var writer = new JsonAppSettingsStore(path);
        await writer.SaveAsync(expected);

        var loaded = await new JsonAppSettingsStore(path).LoadAsync();

        Assert.Equal(expected, loaded);
    }

    [Fact]
    public async Task Save_rejects_a_download_directory_that_no_longer_exists()
    {
        var path = Path.Combine(_directory, "settings.json");
        var settings = new AppSettings(Path.Combine(_directory, "missing"), DownloadFormat.Mp4, VideoQuality.Best, AppTheme.Dark);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => new JsonAppSettingsStore(path).SaveAsync(settings));

        Assert.False(File.Exists(path));
    }

    public void Dispose()
    {
        var settingsPath = Path.Combine(_directory, "settings.json");
        if (File.Exists(settingsPath))
        {
            File.Delete(settingsPath);
        }
    }
}
