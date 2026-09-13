using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace StreamCrate.App.Presentation;

/// <summary>
/// Turns a probed thumbnail URL into an image source for the queue, history and result cards.
/// Sources are cached per URL and decode size so re-rendering a list does not refetch every
/// thumbnail, and only http(s) URLs are accepted so a hostile probe result cannot point the
/// image at a local file.
/// </summary>
internal static class Thumbnails
{
    private const int CacheLimit = 240;
    private static readonly Dictionary<string, BitmapImage> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> Order = new();

    public static ImageSource? Load(Uri? url, int decodePixelWidth)
    {
        if (url is null || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var key = $"{decodePixelWidth}|{url.AbsoluteUri}";
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var image = new BitmapImage
        {
            DecodePixelType = DecodePixelType.Logical,
            DecodePixelWidth = decodePixelWidth,
            UriSource = url,
        };

        Cache[key] = image;
        Order.Enqueue(key);
        while (Order.Count > CacheLimit)
        {
            Cache.Remove(Order.Dequeue());
        }

        return image;
    }
}
