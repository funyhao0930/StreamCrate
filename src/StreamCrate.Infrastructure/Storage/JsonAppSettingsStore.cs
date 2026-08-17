using System.Text.Json;
using StreamCrate.Core.Models;

namespace StreamCrate.Infrastructure.Storage;

public interface IAppSettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public sealed class JsonAppSettingsStore(string path) : IAppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return AppSettings.CreateDefault();
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken)
            ?? AppSettings.CreateDefault();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.DownloadDirectory);
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("設定檔路徑無效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }
}
