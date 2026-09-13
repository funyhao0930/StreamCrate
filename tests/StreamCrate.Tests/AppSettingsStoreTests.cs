using StreamCrate.Core.Models;
using StreamCrate.App.Presentation;
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
        var expected = new AppSettings(
            _directory,
            DownloadFormat.Mp3,
            VideoQuality.P720,
            AppTheme.Light,
            @"D:\Pictures\streamcrate-background.jpg",
            1.25);

        var writer = new JsonAppSettingsStore(path);
        await writer.SaveAsync(expected);

        var loaded = await new JsonAppSettingsStore(path).LoadAsync();

        Assert.Equal(expected, loaded);
    }

    [Fact]
    public async Task Load_legacy_settings_without_background_path_uses_no_custom_background()
    {
        var path = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(path, """
        {
          "downloadDirectory": "D:\\Media",
          "defaultFormat": 0,
          "defaultQuality": 0,
          "theme": 1
        }
        """);

        var loaded = await new JsonAppSettingsStore(path).LoadAsync();

        Assert.Null(loaded.BackgroundImagePath);
        Assert.Equal(1.0, loaded.MotionTempo);
    }

    [Fact]
    public async Task Resolve_background_path_keeps_an_existing_supported_image()
    {
        var imagePath = Path.Combine(_directory, "background.jpg");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(imagePath, "test image placeholder");

        var resolvedPath = BackgroundImagePathResolver.Resolve(imagePath);

        Assert.Equal(imagePath, resolvedPath);
    }

    [Theory]
    [InlineData("missing.png")]
    [InlineData("unsupported.gif")]
    public void Resolve_background_path_rejects_missing_or_unsupported_images(string fileName)
    {
        var resolvedPath = BackgroundImagePathResolver.Resolve(Path.Combine(_directory, fileName));

        Assert.Null(resolvedPath);
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

        var imagePath = Path.Combine(_directory, "background.jpg");
        if (File.Exists(imagePath))
        {
            File.Delete(imagePath);
        }
    }
}
