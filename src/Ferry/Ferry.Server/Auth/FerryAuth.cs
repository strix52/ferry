using System.Buffers.Text;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace Ferry.Server.Auth;

public sealed class AuthConfig
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("rotatedAt")]
    public long? RotatedAt { get; set; }
}

public sealed record PublicInfoAuthDto(
    [property: JsonPropertyName("required")] bool Required,
    [property: JsonPropertyName("paired")] bool Paired,
    [property: JsonPropertyName("createdAt")] long CreatedAt,
    [property: JsonPropertyName("rotatedAt")] long? RotatedAt
);

public sealed record PublicInfoDto(
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("ips")] List<string> Ips,
    [property: JsonPropertyName("urls")] List<string> Urls,
    [property: JsonPropertyName("primary")] string? Primary,
    [property: JsonPropertyName("auth")] PublicInfoAuthDto Auth
);

public sealed class FerryAuth
{
    private readonly string _authFilePath;
    private readonly object _lock = new();
    private AuthConfig _config;

    public FerryAuth(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _authFilePath = Path.Combine(dataDir, "auth.json");
        _config = LoadOrCreateAuthConfig();
    }

    public AuthConfig Config
    {
        get
        {
            lock (_lock)
            {
                return _config;
            }
        }
    }

    private AuthConfig LoadOrCreateAuthConfig()
    {
        lock (_lock)
        {
            if (File.Exists(_authFilePath))
            {
                try
                {
                    var json = File.ReadAllText(_authFilePath);
                    var parsed = JsonSerializer.Deserialize<AuthConfig>(json);
                    if (parsed != null && !string.IsNullOrEmpty(parsed.Token) && parsed.Token.Length >= 24)
                    {
                        return parsed;
                    }
                }
                catch
                {
                    // regenerate below
                }
            }

            var fresh = new AuthConfig
            {
                Token = GenerateToken(),
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                RotatedAt = null,
            };
            SaveAuthConfig(fresh);
            return fresh;
        }
    }

    private void SaveAuthConfig(AuthConfig config)
    {
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_authFilePath, json);
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Base64Url.EncodeToString(bytes);
    }

    public AuthConfig RotateToken()
    {
        lock (_lock)
        {
            var rotated = new AuthConfig
            {
                Token = GenerateToken(),
                CreatedAt = _config.CreatedAt != 0 ? _config.CreatedAt : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                RotatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            _config = rotated;
            SaveAuthConfig(rotated);
            return rotated;
        }
    }

    public static bool IsLoopback(IPAddress? remoteIp)
    {
        if (remoteIp == null) return false;
        if (IPAddress.IsLoopback(remoteIp)) return true;

        var str = remoteIp.ToString();
        return str == "127.0.0.1" || str == "::1" || str == "::ffff:127.0.0.1";
    }

    public static string TokenFrom(HttpRequest request)
    {
        if (request.Headers.TryGetValue("x-ferry-token", out var headerVal) && headerVal.Count > 0)
        {
            var first = headerVal[0];
            if (!string.IsNullOrEmpty(first)) return first;
        }

        if (request.Query.TryGetValue("token", out var queryVal) && queryVal.Count > 0)
        {
            var first = queryVal[0];
            if (!string.IsNullOrEmpty(first)) return first;
        }

        return "";
    }

    public bool IsAuthorized(HttpContext context)
    {
        if (IsLoopback(context.Connection.RemoteIpAddress))
        {
            return true;
        }

        var incomingToken = TokenFrom(context.Request);
        if (string.IsNullOrEmpty(incomingToken))
        {
            return false;
        }

        var incomingBytes = Encoding.UTF8.GetBytes(incomingToken);
        var expectedBytes = Encoding.UTF8.GetBytes(Config.Token);

        if (incomingBytes.Length != expectedBytes.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(incomingBytes, expectedBytes);
    }

    public static List<string> GetLanIps()
    {
        var ips = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                var ipProps = ni.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr.Address))
                    {
                        var ipStr = addr.Address.ToString();
                        if (!ipStr.StartsWith("169.254."))
                        {
                            ips.Add(ipStr);
                        }
                    }
                }
            }
        }
        catch
        {
            // fallback
        }

        int Rank(string ip) =>
            ip.StartsWith("192.168.") ? 0 :
            ip.StartsWith("10.") ? 1 :
            ip.StartsWith("172.") ? 2 : 3;

        return ips.OrderBy(Rank).ToList();
    }

    public PublicInfoDto GetPublicInfo(int port, bool includeToken)
    {
        var ips = GetLanIps();
        var urls = ips.Select(ip => $"http://{ip}:{port}").ToList();

        string WithToken(string u) => includeToken ? $"{u}?token={Uri.EscapeDataString(Config.Token)}" : u;

        var primary = urls.Count > 0 ? WithToken(urls[0]) : null;

        return new PublicInfoDto(
            Port: port,
            Ips: ips,
            Urls: urls.Select(WithToken).ToList(),
            Primary: primary,
            Auth: new PublicInfoAuthDto(
                Required: true,
                Paired: includeToken,
                CreatedAt: Config.CreatedAt,
                RotatedAt: Config.RotatedAt
            )
        );
    }
}
