using System.Text.Json;
using Ferry.Server.Data;
using Ferry.Server.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Ferry.Server.Tests;

public sealed class DatabaseTests : IDisposable
{
    private readonly string _tempDir;

    public DatabaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ferry-db-test-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public void InsertRow_ReadsBack_WithDeletedFalse_AndPinnedAtNull()
    {
        using var db = new FerryDatabase(_tempDir);

        var inserted = db.InsertTextMessage("dev-1", "My Phone", "hello world", 1700000000000L);
        Assert.True(inserted.Id > 0);
        Assert.Equal("text", inserted.Kind);
        Assert.Equal("dev-1", inserted.SenderId);
        Assert.Equal("My Phone", inserted.SenderName);
        Assert.Equal("hello world", inserted.Text);
        Assert.Null(inserted.Filename);
        Assert.Null(inserted.StoredName);
        Assert.Null(inserted.Size);
        Assert.False(inserted.Deleted);
        Assert.Null(inserted.PinnedAt);
        Assert.Equal(1700000000000L, inserted.CreatedAt);

        var readBack = db.GetMessage(inserted.Id);
        Assert.NotNull(readBack);
        Assert.False(readBack.Deleted);
        Assert.Null(readBack.PinnedAt);

        // Check wire DTO JSON serialization
        var dto = readBack.ToDto();
        var json = JsonSerializer.Serialize(dto);
        Assert.Contains("\"deleted\":false", json);
        Assert.Contains("\"pinnedAt\":null", json);
        Assert.Contains("\"filename\":null", json);
        Assert.Contains("\"size\":null", json);
        Assert.DoesNotContain("storedName", json);
        Assert.DoesNotContain("stored_name", json);
    }

    [Fact]
    public void RuntimeMigration_AddsPinnedAt_ToLegacyDatabase()
    {
        Directory.CreateDirectory(_tempDir);
        var dbPath = Path.Combine(_tempDir, "flow.db");

        // Create legacy table without pinned_at
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE messages (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    kind        TEXT NOT NULL,
                    sender_id   TEXT NOT NULL,
                    sender_name TEXT NOT NULL,
                    text        TEXT,
                    filename    TEXT,
                    stored_name TEXT,
                    size        INTEGER,
                    deleted     INTEGER NOT NULL DEFAULT 0,
                    created_at  INTEGER NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        // Now open with FerryDatabase, which must perform the runtime migration
        using (var db = new FerryDatabase(_tempDir))
        {
            var msg = db.InsertTextMessage("sender1", "Laptop", "migration check", 1700000000100L);
            db.SetPinnedAt(msg.Id, 1700000000200L);

            var pins = db.ListPins();
            Assert.Single(pins);
            Assert.Equal(msg.Id, pins[0].Id);
            Assert.Equal(1700000000200L, pins[0].PinnedAt);
        }
    }
}
