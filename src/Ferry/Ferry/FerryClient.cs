using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Ferry;

// Thin client over the existing Ferry HTTP + WebSocket API (see server.js).
// Loopback needs no pairing token: the server bypasses auth for 127.0.0.1.
public sealed partial class FerryClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public FerryClient(Action<string>? log = null, string? baseAddress = null)
    {
        _log = log ?? (_ => { });
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseAddress ?? "http://127.0.0.1:8787"),
        };
    }

    private readonly HttpClient _http;
    private readonly Action<string> _log;
    private ClientWebSocket? _ws;
    private bool _disposed;

    public string SenderId { get; set; } = "unknown";
    public string SenderName { get; set; } = "Device";

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        using var res = await _http.GetAsync(path, ct);
        res.EnsureSuccessStatusCode();
        var stream = await res.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct)
            ?? throw new JsonException($"Empty JSON from {path}.");
    }

    public async Task<IReadOnlyList<FerryMessage>> GetMessagesAsync(CancellationToken ct = default) =>
        await GetJsonAsync<List<FerryMessage>>("/api/messages", ct);

    public async Task<FerryInfo?> GetInfoAsync(CancellationToken ct = default) =>
        await GetJsonAsync<FerryInfo>("/api/info", ct);

    public async Task OpenOnLaptopAsync(int id, CancellationToken ct = default)
    {
        using var res = await _http.PostAsync($"/api/open/{id}", null, ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task RevealOnLaptopAsync(int id, CancellationToken ct = default)
    {
        using var res = await _http.PostAsync($"/api/reveal/{id}", null, ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task DownloadToAsync(
        int id, string destPath, IProgress<double>? progress, CancellationToken ct = default)
    {
        using var res = await _http.GetAsync(
            $"/api/download/{id}", HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength ?? -1;
        using var net = await res.Content.ReadAsStreamAsync(ct);
        using var fs = File.Create(destPath);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await net.ReadAsync(buffer, ct)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total > 0) progress?.Report((double)done / total * 100);
        }
    }

    public async Task ExportMessageToAsync(int id, string destPath, CancellationToken ct = default)
    {
        using var res = await _http.GetAsync($"/api/export-message/{id}", ct);
        res.EnsureSuccessStatusCode();
        using var net = await res.Content.ReadAsStreamAsync(ct);
        using var fs = File.Create(destPath);
        await net.CopyToAsync(fs, ct);
    }

    public void ExportTempFile(int id, string destPath)
    {
        using var res = _http.GetAsync($"/api/download/{id}").GetAwaiter().GetResult();
        res.EnsureSuccessStatusCode();
        using var net = res.Content.ReadAsStream();
        using var fs = File.Create(destPath);
        net.CopyTo(fs);
    }

    public async Task SendMessageAsync(string text, string senderId, string senderName, CancellationToken ct = default)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(new { text, senderId, senderName }),
            Encoding.UTF8, "application/json");
        using var res = await _http.PostAsync("/api/messages", content, ct);
        res.EnsureSuccessStatusCode();
    }

    // Hard delete, server-side: the rows go, and the phone drops them on its
    // next load. deleteFiles decides what happens to the blobs — removed
    // outright, or moved to data/files/kept under their original names, which
    // is the only place they stay findable once the row that named them is
    // gone.
    public async Task<DeleteResult> DeleteMessagesAsync(
        IReadOnlyList<int> ids, bool deleteFiles, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/messages")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { ids, deleteFiles }),
                Encoding.UTF8, "application/json"),
        };
        using var res = await _http.SendAsync(request, ct);
        res.EnsureSuccessStatusCode();
        var stream = await res.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<DeleteResult>(stream, Json, ct)
            ?? new DeleteResult(ids.Count, 0, 0, 0);
    }

    public async Task<IReadOnlyList<FerryMessage>> GetPinsAsync(CancellationToken ct = default) =>
        await GetJsonAsync<List<FerryMessage>>("/api/pins", ct);

    public async Task<IReadOnlyList<FerryMessage>> SetPinnedAsync(int id, bool pinned, CancellationToken ct = default)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(new { pinned }),
            Encoding.UTF8, "application/json");
        using var res = await _http.PutAsync($"/api/messages/{id}/pin", content, ct);
        res.EnsureSuccessStatusCode();
        var stream = await res.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, default, ct);
        if (doc.RootElement.TryGetProperty("pins", out var pinsEl))
        {
            return JsonSerializer.Deserialize<List<FerryMessage>>(pinsEl.GetRawText(), Json) ?? new List<FerryMessage>();
        }
        return new List<FerryMessage>();
    }

    public async Task<StorageStats> GetStorageAsync(CancellationToken ct = default) =>
        await GetJsonAsync<StorageStats>("/api/storage", ct);

    public async Task<CleanupResult> CleanupAsync(int days, CancellationToken ct = default)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(new { days }),
            Encoding.UTF8, "application/json");
        using var res = await _http.PostAsync("/api/cleanup", content, ct);
        res.EnsureSuccessStatusCode();
        var stream = await res.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<CleanupResult>(stream, Json, ct)
            ?? new CleanupResult(0, 0);
    }

    public async Task RotateTokenAsync(CancellationToken ct = default)
    {
        using var res = await _http.PostAsync("/api/auth/rotate", null, ct);
        res.EnsureSuccessStatusCode();
    }

    // Calls onChanged on a thread-pool thread whenever the server broadcasts
    // a thread-affecting event (message / pins / cleanup), and onConnected
    // right after (re)connect so the caller can catch up on missed events.
    // Caller marshals to UI.
    public async Task WatchAsync(
        Func<Task> onChanged,
        CancellationToken ct,
        Func<Task>? onConnected = null,
        Func<IReadOnlyList<PresenceDevice>, Task>? onPresence = null,
        Func<StorageStats, Task>? onStorage = null,
        Func<FerryMessage, Task>? onMessage = null)
    {
        _ws?.Dispose();
        _ws = new ClientWebSocket();
        var host = _http.BaseAddress?.Authority ?? "127.0.0.1:8787";
        var scheme = _http.BaseAddress?.Scheme == "https" ? "wss" : "ws";
        var query = $"senderId={Uri.EscapeDataString(SenderId)}&senderName={Uri.EscapeDataString(SenderName)}";
        await _ws.ConnectAsync(new Uri($"{scheme}://{host}/ws?{query}"), ct);
        _log("ws connected");
        if (onConnected is not null) await onConnected();
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
        {
            var result = await _ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            var payload = Encoding.UTF8.GetString(buffer, 0, result.Count);
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var type = doc.RootElement.TryGetProperty("type", out var t)
                    ? t.GetString() : null;
                if (type == "message")
                {
                    if (onMessage is not null && doc.RootElement.TryGetProperty("message", out var msgEl))
                    {
                        var msg = JsonSerializer.Deserialize<FerryMessage>(msgEl.GetRawText(), Json);
                        if (msg is not null) await onMessage(msg);
                    }
                    await onChanged();
                }
                else if (type is "pins" or "cleanup") await onChanged();
                else if (type == "storage" && onStorage is not null)
                {
                    if (doc.RootElement.TryGetProperty("storage", out var storageEl))
                    {
                        var stats = JsonSerializer.Deserialize<StorageStats>(storageEl.GetRawText(), Json);
                        if (stats is not null) await onStorage(stats);
                    }
                }
                else if (type == "presence" && onPresence is not null)
                {
                    var list = doc.RootElement.TryGetProperty("presence", out var arr)
                        ? arr.EnumerateArray()
                            .Select(e => new PresenceDevice(
                                e.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "",
                                e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""))
                            .ToList()
                        : new List<PresenceDevice>();
                    await onPresence(list);
                }
            }
            catch (JsonException) { /* ignore malformed frames */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ws?.Dispose();
        _http.Dispose();
    }
}
