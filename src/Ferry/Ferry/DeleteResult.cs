using System.Text.Json.Serialization;

namespace Ferry;

// Mirrors the response of DELETE /api/messages in server.js.
//
// FilesKept counts blobs the server moved to data/files/kept rather than
// unlinked; the user is told the number because those files are still on the
// disk, just no longer reachable through Ferry.
public sealed record DeleteResult(
    [property: JsonPropertyName("deleted")] int Deleted,
    [property: JsonPropertyName("filesDeleted")] int FilesDeleted,
    [property: JsonPropertyName("filesKept")] int FilesKept,
    [property: JsonPropertyName("freedBytes")] long FreedBytes);
