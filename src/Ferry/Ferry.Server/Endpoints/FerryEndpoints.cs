using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ferry.Server.Auth;
using Ferry.Server.Data;
using Ferry.Server.Models;
using Ferry.Server.WebSockets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Ferry.Server.Endpoints;

public sealed class FerryEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly FerryDatabase _db;
    private readonly FerryAuth _auth;
    private readonly FerryWebSocketHub _hub;
    private readonly FerryServerConfig _config;
    private readonly long _storageLimitBytes;

    public FerryEndpoints(FerryDatabase db, FerryAuth auth, FerryWebSocketHub hub, FerryServerConfig config)
    {
        _db = db;
        _auth = auth;
        _hub = hub;
        _config = config;

        var limitMb = 2048L;
        if (long.TryParse(Environment.GetEnvironmentVariable("STORAGE_LIMIT_MB"), out var envMb) && envMb > 0)
        {
            limitMb = envMb;
        }
        _storageLimitBytes = limitMb * 1024L * 1024L;
    }

    public async Task HandleRequestAsync(HttpContext context)
    {
        // 1. WebSocket upgrade check
        if (context.WebSockets.IsWebSocketRequest)
        {
            if (context.Request.Path != "/ws" || !_auth.IsAuthorized(context))
            {
                context.Abort();
                return;
            }

            var ws = await context.WebSockets.AcceptWebSocketAsync();
            var qSenderId = context.Request.Query["senderId"].FirstOrDefault();
            var qSenderName = context.Request.Query["senderName"].FirstOrDefault();
            await _hub.HandleWebSocketAsync(ws, qSenderId, qSenderName, context.RequestAborted);
            return;
        }

        var path = context.Request.Path.Value ?? "";
        var method = context.Request.Method;

        try
        {
            // 2. Discovery
            if (method == "GET" && path == "/api/info")
            {
                var isAuth = _auth.IsAuthorized(context);
                var info = _auth.GetPublicInfo(_config.Port, isAuth);
                await SendJsonAsync(context, 200, info);
                return;
            }

            // 3. Rotate auth token
            if (method == "POST" && path == "/api/auth/rotate")
            {
                if (!RequireAuth(context)) return;
                _auth.RotateToken();
                _hub.Broadcast(new { type = "auth", action = "rotated" });
                var fullInfo = _auth.GetPublicInfo(_config.Port, true);
                await SendJsonAsync(context, 200, new { ok = true, auth = fullInfo.Auth, info = fullInfo });
                return;
            }

            // 4. Messages
            if (method == "GET" && path == "/api/messages")
            {
                if (!RequireAuth(context)) return;
                var list = _db.ListMessages().Select(m => m.ToDto()).ToList();
                await SendJsonAsync(context, 200, list);
                return;
            }

            if (method == "POST" && path == "/api/messages")
            {
                if (!RequireAuth(context)) return;
                using var doc = await JsonDocument.ParseAsync(context.Request.Body);
                var root = doc.RootElement;

                var text = root.TryGetProperty("text", out var tProp) && tProp.ValueKind == JsonValueKind.String
                    ? tProp.GetString()?.Trim() ?? ""
                    : "";

                if (string.IsNullOrEmpty(text))
                {
                    await SendErrorAsync(context, 400, "empty");
                    return;
                }

                var senderId = root.TryGetProperty("senderId", out var sidProp) && sidProp.ValueKind == JsonValueKind.String
                    ? sidProp.GetString() ?? "unknown"
                    : "unknown";
                if (senderId.Length > 64) senderId = senderId[..64];

                var senderName = root.TryGetProperty("senderName", out var snameProp) && snameProp.ValueKind == JsonValueKind.String
                    ? snameProp.GetString() ?? "Device"
                    : "Device";
                if (senderName.Length > 40) senderName = senderName[..40];

                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var row = _db.InsertTextMessage(senderId, senderName, text, now);
                var dto = row.ToDto();
                _hub.Broadcast(new { type = "message", message = dto });
                await SendJsonAsync(context, 200, new { ok = true, id = row.Id });
                return;
            }

            // 5. Pins
            if (method == "GET" && path == "/api/pins")
            {
                if (!RequireAuth(context)) return;
                var pins = _db.ListPins().Select(m => m.ToDto()).ToList();
                await SendJsonAsync(context, 200, pins);
                return;
            }

            // Regex match strictly numeric pin route
            var pinMatch = Regex.Match(path, @"^/api/messages/(\d+)/pin$");
            if (method == "PUT" && pinMatch.Success)
            {
                if (!RequireAuth(context)) return;
                var id = long.Parse(pinMatch.Groups[1].Value);
                var row = _db.GetMessage(id);
                if (row == null)
                {
                    await SendErrorAsync(context, 404, "not found");
                    return;
                }

                using var doc = await JsonDocument.ParseAsync(context.Request.Body);
                var pinned = doc.RootElement.TryGetProperty("pinned", out var pProp) && pProp.GetBoolean();

                if (pinned)
                {
                    if (row.PinnedAt == null && _db.ListPins().Count >= 5)
                    {
                        _db.ClearOldestPin();
                    }
                    var nextPinnedAt = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), (_db.GetMaxPinnedAt() ?? 0) + 1);
                    _db.SetPinnedAt(id, nextPinnedAt);
                }
                else
                {
                    _db.ClearPinnedAt(id);
                }

                var updatedPins = _db.ListPins().Select(m => m.ToDto()).ToList();
                _hub.Broadcast(new { type = "pins", pins = updatedPins });
                await SendJsonAsync(context, 200, new { ok = true, pins = updatedPins });
                return;
            }

            // 6. Storage stats
            if (method == "GET" && path == "/api/storage")
            {
                if (!RequireAuth(context)) return;
                var stats = _db.GetStorageStats(_storageLimitBytes);
                await SendJsonAsync(context, 200, stats);
                return;
            }

            // 7. Upload
            if (method == "POST" && path == "/api/upload")
            {
                if (!RequireAuth(context)) return;
                var bodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (bodySizeFeature is { IsReadOnly: false })
                {
                    bodySizeFeature.MaxRequestBodySize = FerryServerInstance.UploadRequestBodyLimit;
                }
                var rawName = context.Request.Query["name"].FirstOrDefault() ?? "file";
                var senderId = context.Request.Query["senderId"].FirstOrDefault() ?? "unknown";
                if (senderId.Length > 64) senderId = senderId[..64];
                var senderName = context.Request.Query["senderName"].FirstOrDefault() ?? "Device";
                if (senderName.Length > 40) senderName = senderName[..40];

                var baseName = Path.GetFileName(rawName);
                var safeName = Regex.Replace(baseName, @"[\u0000-\u001f\\/:*?""<>|]", "_");
                if (string.IsNullOrWhiteSpace(safeName)) safeName = "file";

                var hex = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var storedName = $"{now}_{hex}_{safeName}";
                var destPath = Path.Combine(_db.FilesDir, storedName);

                try
                {
                    await using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await context.Request.Body.CopyToAsync(fs, context.RequestAborted);
                    var bytes = fs.Length;

                    var row = _db.InsertFileMessage(senderId, senderName, safeName, storedName, bytes, now);
                    var dto = row.ToDto();
                    _hub.Broadcast(new { type = "message", message = dto });
                    _hub.Broadcast(new { type = "storage", storage = _db.GetStorageStats(_storageLimitBytes) });
                    await SendJsonAsync(context, 200, new { ok = true, id = row.Id });
                }
                catch (Exception)
                {
                    await SendErrorAsync(context, 500, "write failed");
                }
                return;
            }

            // 8. Download
            if (method == "GET" && path.StartsWith("/api/download/"))
            {
                if (!RequireAuth(context)) return;
                var seg = path["/api/download/".Length..];
                if (!long.TryParse(seg, out var id))
                {
                    await SendErrorAsync(context, 404, "gone");
                    return;
                }

                var row = _db.GetMessage(id);
                if (row == null || row.Kind != "file" || row.Deleted)
                {
                    await SendErrorAsync(context, 404, "gone");
                    return;
                }

                var filePath = Path.Combine(_db.FilesDir, row.StoredName ?? "");
                if (string.IsNullOrEmpty(row.StoredName) || !File.Exists(filePath))
                {
                    await SendErrorAsync(context, 404, "missing");
                    return;
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/octet-stream";
                context.Response.Headers.ContentDisposition = $"attachment; filename=\"{Uri.EscapeDataString(row.Filename ?? "file")}\"";
                context.Response.ContentLength = row.Size;
                await context.Response.SendFileAsync(filePath, context.RequestAborted);
                return;
            }

            // 9. Export message
            if (method == "GET" && path.StartsWith("/api/export-message/"))
            {
                if (!RequireAuth(context)) return;
                var seg = path["/api/export-message/".Length..];
                if (!long.TryParse(seg, out var id))
                {
                    await SendErrorAsync(context, 404, "not found");
                    return;
                }

                var row = _db.GetMessage(id);
                if (row == null || row.Kind != "text")
                {
                    await SendErrorAsync(context, 404, "not found");
                    return;
                }

                var dt = DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAt).UtcDateTime;
                var exportFilename = $"Ferry message {dt:yyyy-MM-dd-HH-mm}.txt";
                var bytes = Encoding.UTF8.GetBytes(row.Text ?? "");

                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/plain; charset=utf-8";
                context.Response.Headers.ContentDisposition = $"attachment; filename=\"{exportFilename}\"";
                context.Response.ContentLength = bytes.Length;
                await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
                return;
            }

            // 10. Open
            if (method == "POST" && path.StartsWith("/api/open/"))
            {
                if (!RequireAuth(context)) return;
                var seg = path["/api/open/".Length..];
                if (!long.TryParse(seg, out var id))
                {
                    await SendErrorAsync(context, 404, "gone");
                    return;
                }

                var row = _db.GetMessage(id);
                if (row == null || row.Kind != "file" || row.Deleted)
                {
                    await SendErrorAsync(context, 404, "gone");
                    return;
                }

                var filePath = Path.GetFullPath(Path.Combine(_db.FilesDir, row.StoredName ?? ""));
                if (string.IsNullOrEmpty(row.StoredName) || !File.Exists(filePath))
                {
                    await SendErrorAsync(context, 404, "missing");
                    return;
                }

                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c start \"\" \"{filePath}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to open file: {ex.Message}");
                }

                await SendJsonAsync(context, 200, new { ok = true });
                return;
            }

            // 11. Reveal
            if (method == "POST" && path.StartsWith("/api/reveal/"))
            {
                if (!RequireAuth(context)) return;
                var seg = path["/api/reveal/".Length..];
                if (!long.TryParse(seg, out var id))
                {
                    await SendErrorAsync(context, 404, "gone");
                    return;
                }

                var row = _db.GetMessage(id);
                if (row == null || row.Kind != "file" || row.Deleted)
                {
                    await SendErrorAsync(context, 404, "gone");
                    return;
                }

                var filePath = Path.GetFullPath(Path.Combine(_db.FilesDir, row.StoredName ?? ""));
                if (string.IsNullOrEmpty(row.StoredName) || !File.Exists(filePath))
                {
                    await SendErrorAsync(context, 404, "missing");
                    return;
                }

                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{filePath}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to reveal file: {ex.Message}");
                }

                await SendJsonAsync(context, 200, new { ok = true });
                return;
            }

            // 12. Delete
            if (method == "DELETE" && path == "/api/messages")
            {
                if (!RequireAuth(context)) return;
                using var doc = await JsonDocument.ParseAsync(context.Request.Body);
                if (!doc.RootElement.TryGetProperty("ids", out var idsProp) || idsProp.ValueKind != JsonValueKind.Array)
                {
                    await SendErrorAsync(context, 400, "ids required");
                    return;
                }

                var idList = new HashSet<long>();
                foreach (var item in idsProp.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var id))
                    {
                        idList.Add(id);
                    }
                }

                if (idList.Count == 0)
                {
                    await SendErrorAsync(context, 400, "ids required");
                    return;
                }

                if (idList.Count > 1000)
                {
                    await SendErrorAsync(context, 400, "too many ids");
                    return;
                }

                var deleteFiles = doc.RootElement.TryGetProperty("deleteFiles", out var dfProp) && dfProp.GetBoolean();
                var result = _db.DeleteMessages(idList, deleteFiles);

                if (result.Deleted > 0)
                {
                    _hub.Broadcast(new { type = "cleanup", removed = result.Deleted });
                }
                if (result.TouchedPins)
                {
                    _hub.Broadcast(new { type = "pins", pins = _db.ListPins().Select(m => m.ToDto()).ToList() });
                }
                if (result.TouchedFiles)
                {
                    _hub.Broadcast(new { type = "storage", storage = _db.GetStorageStats(_storageLimitBytes) });
                }

                await SendJsonAsync(context, 200, new
                {
                    ok = true,
                    deleted = result.Deleted,
                    filesDeleted = result.FilesDeleted,
                    filesKept = result.FilesKept,
                    freedBytes = result.FreedBytes
                });
                return;
            }

            // 13. Cleanup
            if (method == "POST" && path == "/api/cleanup")
            {
                if (!RequireAuth(context)) return;
                using var doc = await JsonDocument.ParseAsync(context.Request.Body);
                double days;
                if (doc.RootElement.TryGetProperty("days", out var daysProp))
                {
                    if (daysProp.ValueKind == JsonValueKind.Null)
                    {
                        days = 0.0;
                    }
                    else if (daysProp.ValueKind == JsonValueKind.Number && daysProp.TryGetDouble(out var d))
                    {
                        days = d;
                    }
                    else
                    {
                        await SendErrorAsync(context, 400, "days must be between 0 and 3650");
                        return;
                    }
                }
                else
                {
                    await SendErrorAsync(context, 400, "days must be between 0 and 3650");
                    return;
                }

                if (double.IsNaN(days) || double.IsInfinity(days) || days < 0.0 || days > 3650.0)
                {
                    await SendErrorAsync(context, 400, "days must be between 0 and 3650");
                    return;
                }

                var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)(days * 86400000.0);
                var result = _db.TombstoneFileMessagesBefore(cutoff);

                _hub.Broadcast(new { type = "cleanup", removed = result.Removed });
                _hub.Broadcast(new { type = "storage", storage = _db.GetStorageStats(_storageLimitBytes) });

                await SendJsonAsync(context, 200, new { ok = true, removed = result.Removed, freedBytes = result.FreedBytes });
                return;
            }

            // 14. Fallback to static file serving
            await ServeStaticFileAsync(context, path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Request failed: {ex.GetType().Name}: {ex.Message}");
            if (!context.Response.HasStarted)
            {
                await SendErrorAsync(context, 500, "request failed");
            }
        }
    }

    private bool RequireAuth(HttpContext context)
    {
        if (_auth.IsAuthorized(context))
        {
            return true;
        }

        context.Response.StatusCode = 401;
        context.Response.ContentType = "application/json; charset=utf-8";
        _ = context.Response.WriteAsync("{\"error\":\"pairing required\"}");
        return false;
    }

    private async Task ServeStaticFileAsync(HttpContext context, string urlPath)
    {
        var rawPath = context.Request.Path.Value ?? "";
        var decodedPath = Uri.UnescapeDataString(rawPath);

        if (decodedPath.Contains(".."))
        {
            await SendErrorAsync(context, 403, "forbidden");
            return;
        }

        var rel = decodedPath == "/" ? "index.html" : decodedPath.TrimStart('/');
        var publicDir = Path.GetFullPath(_config.PublicDir);
        var combined = Path.GetFullPath(Path.Combine(publicDir, rel));

        var normalizedPublic = publicDir;
        if (!normalizedPublic.EndsWith(Path.DirectorySeparatorChar.ToString()))
        {
            normalizedPublic += Path.DirectorySeparatorChar;
        }

        if (combined != publicDir && !combined.StartsWith(normalizedPublic, StringComparison.OrdinalIgnoreCase))
        {
            await SendErrorAsync(context, 403, "forbidden");
            return;
        }

        if (!File.Exists(combined))
        {
            await SendErrorAsync(context, 404, "not found");
            return;
        }

        var ext = Path.GetExtension(combined).ToLowerInvariant();
        var contentType = ext switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".json" => "application/json; charset=utf-8",
            _ => "application/octet-stream",
        };

        context.Response.StatusCode = 200;
        context.Response.ContentType = contentType;
        await context.Response.SendFileAsync(combined, context.RequestAborted);
    }

    private static async Task SendJsonAsync(HttpContext context, int statusCode, object body)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        var json = JsonSerializer.Serialize(body, JsonOptions);
        await context.Response.WriteAsync(json);
    }

    private static Task SendErrorAsync(HttpContext context, int statusCode, string message) =>
        SendJsonAsync(context, statusCode, new { error = message });
}
