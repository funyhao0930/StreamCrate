using System.Text.RegularExpressions;

namespace StreamCrate.Infrastructure.Diagnostics;

public static partial class SensitiveDataSanitizer
{
    [GeneratedRegex("https?://[^\\s\\\"']+", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    public static string Sanitize(string message) => UrlPattern().Replace(message, match =>
    {
        return Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Path)
            : match.Value.Split('?', 2)[0];
    });
}
