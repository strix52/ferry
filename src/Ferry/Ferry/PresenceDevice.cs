using System.Text.Json.Serialization;

namespace Ferry;

// One entry of the server's "presence" broadcast (see server.js):
// the devices currently holding a live socket.
public sealed record PresenceDevice(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);
