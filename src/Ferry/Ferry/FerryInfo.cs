using System.Text.Json.Serialization;

namespace Ferry;

// Mirrors publicInfo() in server.js: { port, ips, urls, primary, auth }.
// Over loopback the server marks the caller paired and includes the token
// in the urls, exactly as the web client's Connect panel consumes them.
public sealed record FerryInfo(
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("ips")] List<string> Ips,
    [property: JsonPropertyName("urls")] List<string> Urls,
    [property: JsonPropertyName("primary")] string? Primary);
