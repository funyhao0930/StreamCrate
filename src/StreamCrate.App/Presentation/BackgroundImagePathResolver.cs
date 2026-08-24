namespace StreamCrate.App.Presentation;

public static class BackgroundImagePathResolver
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp",
        ".jpeg",
        ".jpg",
        ".png",
        ".webp",
    };

    public static string? Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !SupportedExtensions.Contains(Path.GetExtension(path)))
        {
            return null;
        }

        return File.Exists(path) ? path : null;
    }
}
