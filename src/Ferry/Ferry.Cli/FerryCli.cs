using System.Net;
using System.Text.Json;

namespace Ferry.Cli;

internal static class FerryCli
{
    private const int UsageError = 2;
    private const int ServerUnavailable = 3;
    private const int PhoneNotConnected = 4;
    private const int ItemNotFound = 5;
    private const int RequestFailed = 6;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        HttpMessageHandler? handler = null,
        string? localAppData = null,
        string? userProfile = null,
        CancellationToken ct = default)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            await WriteAsync(output, new
            {
                ok = true,
                usage = new[]
                {
                    "ferryctl status",
                    "ferryctl send-text <text>",
                    "ferryctl send-file <path>",
                    "ferryctl recent [--limit N]",
                    "ferryctl read <id|latest>",
                    "ferryctl pull <id|latest> [--to <path>]",
                },
            });
            return 0;
        }

        try
        {
            var baseAddress = Environment.GetEnvironmentVariable("FERRY_URL") ?? "http://127.0.0.1:8787";
            if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return await FailAsync(error, UsageError, "invalid_url", "FERRY_URL must be an absolute HTTP URL.");
            using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            http.BaseAddress = uri;
            http.Timeout = TimeSpan.FromSeconds(10);
            var device = LoadIdentity(localAppData);
            var client = new FerryAgentClient(http, device);

            switch (args[0])
            {
                case "status":
                {
                    if (args.Length != 1)
                        return await FailAsync(error, UsageError, "usage", "Usage: ferryctl status");
                    var devices = await client.GetOtherDevicesAsync(ct);
                    await WriteAsync(output, new
                    {
                        ok = true,
                        server = true,
                        phoneConnected = devices.Count > 0,
                        devices = devices.Select(item => new { item.Name }),
                    });
                    return 0;
                }
                case "send-text":
                {
                    if (args.Length < 2 || string.IsNullOrWhiteSpace(string.Join(' ', args.Skip(1))))
                        return await FailAsync(error, UsageError, "usage", "Usage: ferryctl send-text <text>");
                    if (!await RequirePhoneAsync(client, error, ct)) return PhoneNotConnected;
                    var text = string.Join(' ', args.Skip(1));
                    var id = await client.SendTextAsync(text, ct);
                    await WriteAsync(output, new { ok = true, id, kind = "text" });
                    return 0;
                }
                case "send-file":
                {
                    if (args.Length != 2)
                        return await FailAsync(error, UsageError, "usage", "Usage: ferryctl send-file <path>");
                    var path = Path.GetFullPath(args[1]);
                    if (!File.Exists(path))
                        return await FailAsync(error, ItemNotFound, "file_not_found", $"File not found: {path}");
                    if (!await RequirePhoneAsync(client, error, ct)) return PhoneNotConnected;
                    var id = await client.SendFileAsync(path, ct);
                    await WriteAsync(output, new { ok = true, id, kind = "file", path });
                    return 0;
                }
                case "recent":
                {
                    var limit = ParseLimit(args);
                    if (limit is null) return await FailAsync(error, UsageError, "usage", "Usage: ferryctl recent [--limit 1..100]");
                    var messages = await client.GetRecentAsync(limit.Value, ct);
                    await WriteAsync(output, new
                    {
                        ok = true,
                        messages = messages.Select(message => new
                        {
                            message.Id,
                            message.Kind,
                            message.SenderName,
                            text = Preview(message.Text),
                            message.Filename,
                            message.Size,
                            message.CreatedAt,
                            truncated = message.Text is { Length: > 200 },
                        }),
                    });
                    return 0;
                }
                case "read":
                {
                    if (args.Length != 2)
                        return await FailAsync(error, UsageError, "usage", "Usage: ferryctl read <id|latest>");
                    var message = await client.FindTextAsync(args[1], ct);
                    if (message is null)
                        return await FailAsync(error, ItemNotFound, "message_not_found", "No available Ferry text message matched.");
                    await WriteAsync(output, new
                    {
                        ok = true,
                        message.Id,
                        message.SenderName,
                        message.Text,
                        message.CreatedAt,
                    });
                    return 0;
                }
                case "pull":
                {
                    if (args.Length != 2 && (args.Length != 4 || args[2] != "--to" || string.IsNullOrWhiteSpace(args[3])))
                        return await FailAsync(error, UsageError, "usage", "Usage: ferryctl pull <id|latest> [--to <path>]");
                    var message = await client.FindFileAsync(args[1], ct);
                    if (message is null)
                        return await FailAsync(error, ItemNotFound, "file_not_found", "No available Ferry file matched.");
                    var destination = args.Length == 4 ? args[3] : DefaultDownloadDirectory(userProfile);
                    var path = await client.PullAsync(message, destination, ct);
                    await WriteAsync(output, new { ok = true, id = message.Id, path });
                    return 0;
                }
                default:
                    return await FailAsync(error, UsageError, "unknown_command", $"Unknown command: {args[0]}");
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not null)
        {
            return await FailAsync(error, RequestFailed, "request_failed",
                $"Ferry rejected the request (HTTP {(int)ex.StatusCode.Value}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return await FailAsync(error, ServerUnavailable, "ferry_not_running", "Ferry is not running on this laptop.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return await FailAsync(error, ItemNotFound, "file_error", ex.Message);
        }
    }

    private static async Task<bool> RequirePhoneAsync(FerryAgentClient client, TextWriter error, CancellationToken ct)
    {
        if ((await client.GetOtherDevicesAsync(ct)).Count > 0) return true;
        await WriteAsync(error, new
        {
            ok = false,
            code = "phone_not_connected",
            message = "Open Ferry on your phone and keep it connected.",
        });
        return false;
    }

    private static int? ParseLimit(string[] args)
    {
        if (args.Length == 1) return 10;
        return args.Length == 3 && args[1] == "--limit" && int.TryParse(args[2], out var limit) && limit is >= 1 and <= 100
            ? limit
            : null;
    }

    private static string? Preview(string? text) =>
        text is { Length: > 200 } ? text[..200] : text;

    private static DeviceIdentity LoadIdentity(string? localAppData)
    {
        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(localAppData, "Ferry", "device.json");
        try
        {
            var identity = JsonSerializer.Deserialize<DeviceIdentity>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (identity is { Id.Length: > 0, Name.Length: > 0 }) return identity;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new DeviceIdentity("ferryctl", "Laptop");
    }

    private static string DefaultDownloadDirectory(string? userProfile)
    {
        var drive = DriveInfo.GetDrives().FirstOrDefault(candidate =>
            string.Equals(candidate.Name, @"D:\", StringComparison.OrdinalIgnoreCase)
            && candidate.IsReady
            && candidate.DriveType == DriveType.Fixed);
        if (drive is not null) return Path.Combine(drive.RootDirectory.FullName, "Ferry", "Downloads") + Path.DirectorySeparatorChar;
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, "Downloads", "Ferry") + Path.DirectorySeparatorChar;
    }

    private static async Task<int> FailAsync(TextWriter error, int exitCode, string code, string message)
    {
        await WriteAsync(error, new { ok = false, code, message });
        return exitCode;
    }

    private static Task WriteAsync(TextWriter writer, object value) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions));
}
