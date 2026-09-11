using Ferry.Server.Models;

namespace Ferry.Server.Data;

public sealed record FerryMessageRecord(
    long Id,
    string Kind,
    string SenderId,
    string SenderName,
    string? Text,
    string? Filename,
    string? StoredName,
    long? Size,
    bool Deleted,
    long? PinnedAt,
    long CreatedAt
)
{
    public FerryMessageDto ToDto() => new(
        Id,
        Kind,
        SenderId,
        SenderName,
        Text,
        Filename,
        Size,
        Deleted,
        PinnedAt,
        CreatedAt
    );
}
