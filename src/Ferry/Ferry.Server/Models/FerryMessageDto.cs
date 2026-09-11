using System.Text.Json.Serialization;

namespace Ferry.Server.Models;

public sealed record FerryMessageDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("senderId")] string SenderId,
    [property: JsonPropertyName("senderName")] string SenderName,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("filename")] string? Filename,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("deleted")] bool Deleted,
    [property: JsonPropertyName("pinnedAt")] long? PinnedAt,
    [property: JsonPropertyName("createdAt")] long CreatedAt
);
