using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ferry.Cli;

internal sealed record DeviceIdentity(string Id, string Name);

internal sealed record PresenceDevice(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);

internal sealed record PresenceResponse(
    [property: JsonPropertyName("presence")] List<PresenceDevice> Presence);

internal sealed record MessageItem(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("senderId")] string SenderId,
    [property: JsonPropertyName("senderName")] string SenderName,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("filename")] string? Filename,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("deleted")] bool Deleted,
    [property: JsonPropertyName("createdAt")] long CreatedAt);

internal sealed class FerryAgentClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly DeviceIdentity _device;

    public FerryAgentClient(HttpClient http, DeviceIdentity device)
    {
        _http = http;
        _device = device;
    }

    public async Task<List<PresenceDevice>> GetOtherDevicesAsync(CancellationToken ct = default)
    {
        var response = await _http.GetFromJsonAsync<PresenceResponse>("/api/presence", JsonOptions, ct)
            ?? new PresenceResponse([]);
        return response.Presence
            .Where(device => !string.Equals(device.Id, _device.Id, StringComparison.Ordinal))
            .ToList();
    }

    public async Task<long> SendTextAsync(string text, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("/api/messages", new
        {
            text,
            senderId = _device.Id,
            senderName = _device.Name,
        }, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        return await ReadIdAsync(response, ct);
    }

    public async Task<long> SendFileAsync(string path, CancellationToken ct = default)
    {
        var name = Path.GetFileName(path);
        var url = "/api/upload?name=" + Uri.EscapeDataString(name)
            + "&senderId=" + Uri.EscapeDataString(_device.Id)
            + "&senderName=" + Uri.EscapeDataString(_device.Name);
        await using var stream = File.OpenRead(path);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new("application/octet-stream");
        using var response = await _http.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();
        return await ReadIdAsync(response, ct);
    }

    public async Task<List<MessageItem>> GetRecentAsync(int limit, CancellationToken ct = default)
    {
        var messages = await _http.GetFromJsonAsync<List<MessageItem>>("/api/messages", JsonOptions, ct) ?? [];
        return messages.OrderByDescending(message => message.Id).Take(limit).ToList();
    }

    public async Task<MessageItem?> FindTextAsync(string idOrLatest, CancellationToken ct = default)
    {
        var messages = await _http.GetFromJsonAsync<List<MessageItem>>("/api/messages", JsonOptions, ct) ?? [];
        var texts = messages.Where(message => message.Kind == "text" && !message.Deleted);
        if (string.Equals(idOrLatest, "latest", StringComparison.OrdinalIgnoreCase))
        {
            return texts.MaxBy(message => message.Id);
        }
        return long.TryParse(idOrLatest, out var id) ? texts.FirstOrDefault(message => message.Id == id) : null;
    }

    public async Task<MessageItem?> FindFileAsync(string idOrLatest, CancellationToken ct = default)
    {
        var messages = await _http.GetFromJsonAsync<List<MessageItem>>("/api/messages", JsonOptions, ct) ?? [];
        var files = messages.Where(message => message.Kind == "file" && !message.Deleted);
        if (string.Equals(idOrLatest, "latest", StringComparison.OrdinalIgnoreCase))
        {
            return files.MaxBy(message => message.Id);
        }
        return long.TryParse(idOrLatest, out var id) ? files.FirstOrDefault(message => message.Id == id) : null;
    }

    public async Task<string> PullAsync(MessageItem message, string destination, CancellationToken ct = default)
    {
        var path = ResolveDestination(destination, message.Filename ?? $"ferry-{message.Id}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var response = await _http.GetAsync($"/api/download/{message.Id}", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, ct);
        return path;
    }

    internal static string ResolveDestination(string destination, string filename)
    {
        var full = Path.GetFullPath(destination);
        var path = Directory.Exists(full) || destination.EndsWith(Path.DirectorySeparatorChar)
            ? Path.Combine(full, filename)
            : full;
        if (!File.Exists(path)) return path;

        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var suffix = 1; ; suffix++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({suffix}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static async Task<long> ReadIdAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.GetProperty("id").GetInt64();
    }
}
