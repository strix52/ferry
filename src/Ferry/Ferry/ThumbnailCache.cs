using System.Windows.Media.Imaging;

namespace Ferry;

// UI-thread-only cache of decoded thread thumbnails, keyed by message id.
//
// It lives outside FerryMessage on purpose: FerryMessage is a record, and the
// synthesised equality compares every instance field, so caching the bitmap in
// a field would make an otherwise-unchanged message compare unequal to itself
// and force a rebind (and a re-download) on every refresh.
internal static class ThumbnailCache
{
    // Decode wider than the 180px bubble cap so the image stays crisp on
    // 150%/200% displays without holding the full-resolution bitmap.
    private const int DecodeWidth = 420;
    private const int MaxEntries = 240;

    private static readonly Dictionary<int, BitmapImage> Cache = new();

    public static BitmapImage? Get(int id, string url)
    {
        if (Cache.TryGetValue(id, out var hit)) return hit;
        BitmapImage image;
        try
        {
            image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(url);
            image.DecodePixelWidth = DecodeWidth;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
        }
        catch (Exception ex)
        {
            // Unsupported codec, malformed URI, or a server that went away.
            // A missing thumbnail is a cosmetic loss; the file card still works.
            App.Log($"thumb {id} failed: {ex.Message}");
            return null;
        }
        if (Cache.Count >= MaxEntries) Cache.Clear();
        Cache[id] = image;
        return image;
    }

    public static void Forget(int id) => Cache.Remove(id);
}
