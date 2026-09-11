using System.Text.Json.Serialization;

namespace Ferry;

// Mirrors storageStats() in server.js:
// { fileBytes, fileCount, messageCount, limitBytes, overLimit }
public sealed record StorageStats(
    [property: JsonPropertyName("fileBytes")] long FileBytes,
    [property: JsonPropertyName("fileCount")] int FileCount,
    [property: JsonPropertyName("messageCount")] int MessageCount,
    [property: JsonPropertyName("limitBytes")] long LimitBytes,
    [property: JsonPropertyName("overLimit")] bool OverLimit);

// Mirrors POST /api/cleanup response in server.js:
// { ok, removed, freedBytes }
public sealed record CleanupResult(
    [property: JsonPropertyName("removed")] int Removed,
    [property: JsonPropertyName("freedBytes")] long FreedBytes);
