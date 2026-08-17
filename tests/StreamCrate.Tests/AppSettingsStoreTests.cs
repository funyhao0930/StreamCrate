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
        var expected = new AppSettings(@"D:\Media", DownloadFormat.Mp3, VideoQuality.P720, AppTheme.Light);

        var writer = new JsonAppSettingsStore(path);
        await writer.SaveAsync(expected);

        var loaded = await new JsonAppSettingsStore(path).LoadAsync();

        Assert.Equal(expected, loaded);
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
