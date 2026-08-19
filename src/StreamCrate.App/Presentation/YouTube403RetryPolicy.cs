using StreamCrate.Infrastructure.Processes;

namespace StreamCrate.App.Presentation;

internal static class YouTube403RetryPolicy
{
    public static bool ShouldRetry(string extractor, string message) =>
        string.Equals(extractor, "Youtube", StringComparison.OrdinalIgnoreCase) &&
        message.Contains("unable to download video data", StringComparison.OrdinalIgnoreCase) &&
        message.Contains("HTTP Error 403: Forbidden", StringComparison.OrdinalIgnoreCase);

    public static ProcessSpecification WithForceIpv4(ProcessSpecification specification)
    {
        if (specification.Arguments.Contains("--force-ipv4", StringComparer.Ordinal))
        {
            return specification;
        }

        return specification with
        {
            Arguments = ["--force-ipv4", .. specification.Arguments],
            RedactedDisplayArguments = ["--force-ipv4", .. specification.RedactedDisplayArguments],
        };
    }
}
