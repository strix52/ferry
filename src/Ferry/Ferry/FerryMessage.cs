using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace Ferry;

// Mirrors the server's msgToClient() payload in server.js:
// { id, kind, senderId, senderName, text, filename, size,
//   deleted, pinnedAt, createdAt }
public sealed record FerryMessage(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("senderId")] string SenderId,
    [property: JsonPropertyName("senderName")] string SenderName,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("filename")] string? Filename,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("deleted")] bool Deleted,
    [property: JsonPropertyName("pinnedAt")] long? PinnedAt,
    [property: JsonPropertyName("createdAt")] long CreatedAt,
    bool Mine = false)
{
    public bool IsText => Kind == "text";

    public bool CanAct => !Deleted;

    public bool IsImage => !IsText && ThumbExtensions.Contains(Extension);

    public bool ShowThumb => CanAct && IsImage;

    public string DownloadUrl => $"{FerryEndpoint.BaseAddress}/api/download/{Id}";

    // Decoded lazily and shared through ThumbnailCache so that re-fetching the
    // thread does not re-download every image. Null when there is nothing to
    // show or the codec refused the bytes.
    public BitmapImage? Thumb => ShowThumb ? ThumbnailCache.Get(Id, DownloadUrl) : null;

    public string Extension =>
        Filename?.Contains('.') == true
            ? Filename[(Filename.LastIndexOf('.') + 1)..].ToLowerInvariant()
            : "";

    // Only formats WIC actually decodes on Windows 11. svg and avif were listed
    // here before: neither has a stock decoder, so ShowThumb went true and the
    // bubble rendered an empty gap where the picture should have been.
    private static readonly HashSet<string> ThumbExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "gif", "webp", "bmp", "tif", "tiff", "ico",
    };

    public string DisplayName => Mine ? "You" : SenderName;

    public string TimeText =>
        DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt).ToLocalTime().ToString("HH:mm");

    public string SizeText => FormatBytes(Size ?? 0);

    public string Headline =>
        IsText ? (Text ?? "") : Deleted ? $"{Filename} · removed" : (Filename ?? "");

    private static string FormatBytes(long n)
    {
        if (n <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var i = (int)Math.Floor(Math.Log(n) / Math.Log(1024));
        if (i == 0) return $"{n} {units[0]}";
        var value = n / Math.Pow(1024, i);
        return $"{value:F1} {units[i]}";
    }
}
