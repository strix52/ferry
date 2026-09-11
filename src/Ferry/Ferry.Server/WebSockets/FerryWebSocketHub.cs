using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ferry.Server.WebSockets;

public sealed record PresenceDeviceDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name
);

public sealed class FerryWebSocketHub : IAsyncDisposable
{
    private sealed class ClientInfo
    {
        public string SenderId { get; set; } = "unknown";
        public string SenderName { get; set; } = "Device";
    }

    private readonly ConcurrentDictionary<WebSocket, ClientInfo> _clients = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task HandleWebSocketAsync(WebSocket webSocket, string? senderId, string? senderName, CancellationToken ct)
    {
        var id = string.IsNullOrEmpty(senderId) ? "unknown" : senderId;
        if (id.Length > 64) id = id[..64];

        var name = string.IsNullOrEmpty(senderName) ? "Device" : senderName;
        if (name.Length > 40) name = name[..40];

        var info = new ClientInfo
        {
            SenderId = id,
            SenderName = name,
        };

        _clients[webSocket] = info;
        BroadcastPresence();

        var buffer = new byte[1024 * 4];
        try
        {
            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch
        {
            // Socket closed or aborted
        }
        finally
        {
            if (_clients.TryRemove(webSocket, out _))
            {
                BroadcastPresence();
            }

            try
            {
                if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
                {
                    await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
            }
            catch
            {
                // ignore
            }
            webSocket.Dispose();
        }
    }

    public List<PresenceDeviceDto> GetPresenceList()
    {
        var seen = new Dictionary<string, string>();
        foreach (var info in _clients.Values)
        {
            if (!seen.ContainsKey(info.SenderId))
            {
                seen[info.SenderId] = info.SenderName;
            }
        }
        return seen.Select(kv => new PresenceDeviceDto(kv.Key, kv.Value)).ToList();
    }

    public void BroadcastPresence()
    {
        Broadcast(new
        {
            type = "presence",
            presence = GetPresenceList()
        });
    }

    public void Broadcast<T>(T payload)
    {
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);

        foreach (var ws in _clients.Keys)
        {
            if (ws.State != WebSocketState.Open) continue;

            _ = Task.Run(async () =>
            {
                try
                {
                    await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch
                {
                    if (_clients.TryRemove(ws, out _))
                    {
                        BroadcastPresence();
                    }
                }
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var ws in _clients.Keys)
        {
            try
            {
                ws.Abort();
            }
            catch
            {
                // ignore
            }
        }
        _clients.Clear();
        await ValueTask.CompletedTask;
    }
}
