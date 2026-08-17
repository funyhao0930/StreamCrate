using System.Diagnostics;
using StreamCrate.Core.Models;
using StreamCrate.Infrastructure.Diagnostics;

namespace StreamCrate.Infrastructure.Processes;

public sealed class YtDlpProcessRunner
{
    public async Task RunAsync(ProcessSpecification specification, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(specification.ExecutablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in specification.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("無法啟動 yt-dlp。");
        using var registration = cancellationToken.Register(() =>
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        });

        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            if (YtDlpProgressParser.TryParse(line) is { } parsed)
            {
                progress?.Report(parsed);
            }
        }

        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "下載失敗。" : SensitiveDataSanitizer.Sanitize(error));
        }
    }
}
