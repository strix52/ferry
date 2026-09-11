using Microsoft.Data.Sqlite;

namespace Ferry.Server.Data;

public sealed record StorageStatsResult(
    long FileBytes,
    long FileCount,
    long MessageCount,
    long LimitBytes,
    bool OverLimit
);

public sealed record DeleteMessagesResult(
    int Deleted,
    int FilesDeleted,
    int FilesKept,
    long FreedBytes,
    bool TouchedPins,
    bool TouchedFiles
);

public sealed record CleanupResult(
    int Removed,
    long FreedBytes
);

public sealed class FerryDatabase : IDisposable
{
    private readonly string _dataDir;
    private readonly string _filesDir;
    private readonly string _keptDir;
    private readonly SqliteConnection _connection;
    private readonly object _lock = new();

    public string DataDir => _dataDir;
    public string FilesDir => _filesDir;
    public string KeptDir => _keptDir;

    public FerryDatabase(string dataDir)
    {
        _dataDir = Path.GetFullPath(dataDir);
        _filesDir = Path.Combine(_dataDir, "files");
        _keptDir = Path.Combine(_filesDir, "kept");

        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_filesDir);
        Directory.CreateDirectory(_keptDir);

        var dbPath = Path.Combine(_dataDir, "flow.db");
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };

        _connection = new SqliteConnection(builder.ConnectionString);
        _connection.Open();

        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        lock (_lock)
        {
            using var pragmaCmd = _connection.CreateCommand();
            pragmaCmd.CommandText = "PRAGMA journal_mode = WAL;";
            pragmaCmd.ExecuteNonQuery();

            using var createCmd = _connection.CreateCommand();
            createCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS messages (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    kind        TEXT NOT NULL,            -- 'text' | 'file'
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
            createCmd.ExecuteNonQuery();

            // Runtime migration for pinned_at
            var hasPinnedAt = false;
            using (var checkCmd = _connection.CreateCommand())
            {
                checkCmd.CommandText = "PRAGMA table_info(messages);";
                using var reader = checkCmd.ExecuteReader();
                while (reader.Read())
                {
                    var colName = reader.GetString(1);
                    if (string.Equals(colName, "pinned_at", StringComparison.OrdinalIgnoreCase))
                    {
                        hasPinnedAt = true;
                        break;
                    }
                }
            }

            if (!hasPinnedAt)
            {
                using var alterCmd = _connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE messages ADD COLUMN pinned_at INTEGER;";
                alterCmd.ExecuteNonQuery();
            }
        }
    }

    public FerryMessageRecord InsertTextMessage(string senderId, string senderName, string text, long createdAt)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO messages (kind, sender_id, sender_name, text, filename, stored_name, size, created_at)
                VALUES ('text', @senderId, @senderName, @text, NULL, NULL, NULL, @createdAt);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@senderId", senderId);
            cmd.Parameters.AddWithValue("@senderName", senderName);
            cmd.Parameters.AddWithValue("@text", text);
            cmd.Parameters.AddWithValue("@createdAt", createdAt);

            var id = (long)cmd.ExecuteScalar()!;
            return GetMessage(id)!;
        }
    }

    public FerryMessageRecord InsertFileMessage(string senderId, string senderName, string filename, string storedName, long size, long createdAt)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO messages (kind, sender_id, sender_name, text, filename, stored_name, size, created_at)
                VALUES ('file', @senderId, @senderName, NULL, @filename, @storedName, @size, @createdAt);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@senderId", senderId);
            cmd.Parameters.AddWithValue("@senderName", senderName);
            cmd.Parameters.AddWithValue("@filename", filename);
            cmd.Parameters.AddWithValue("@storedName", storedName);
            cmd.Parameters.AddWithValue("@size", size);
            cmd.Parameters.AddWithValue("@createdAt", createdAt);

            var id = (long)cmd.ExecuteScalar()!;
            return GetMessage(id)!;
        }
    }

    public FerryMessageRecord? GetMessage(long id)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, kind, sender_id, sender_name, text, filename, stored_name, size, deleted, pinned_at, created_at
                FROM messages WHERE id = @id;
                """;
            cmd.Parameters.AddWithValue("@id", id);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return ReadRecord(reader);
            }
            return null;
        }
    }

    public List<FerryMessageRecord> ListMessages()
    {
        lock (_lock)
        {
            var list = new List<FerryMessageRecord>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, kind, sender_id, sender_name, text, filename, stored_name, size, deleted, pinned_at, created_at
                FROM messages ORDER BY id ASC;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(ReadRecord(reader));
            }
            return list;
        }
    }

    public List<FerryMessageRecord> ListPins()
    {
        lock (_lock)
        {
            var list = new List<FerryMessageRecord>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, kind, sender_id, sender_name, text, filename, stored_name, size, deleted, pinned_at, created_at
                FROM messages WHERE pinned_at IS NOT NULL ORDER BY pinned_at DESC, id DESC LIMIT 5;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(ReadRecord(reader));
            }
            return list;
        }
    }

    public long? GetMaxPinnedAt()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT MAX(pinned_at) FROM messages WHERE pinned_at IS NOT NULL;";
            var val = cmd.ExecuteScalar();
            if (val == null || val is DBNull) return null;
            return Convert.ToInt64(val);
        }
    }

    public void SetPinnedAt(long id, long pinnedAt)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE messages SET pinned_at = @pinnedAt WHERE id = @id;";
            cmd.Parameters.AddWithValue("@pinnedAt", pinnedAt);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void ClearPinnedAt(long id)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE messages SET pinned_at = NULL WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void ClearOldestPin()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE messages SET pinned_at = NULL
                WHERE id = (SELECT id FROM messages WHERE pinned_at IS NOT NULL ORDER BY pinned_at ASC, id ASC LIMIT 1);
                """;
            cmd.ExecuteNonQuery();
        }
    }

    public StorageStatsResult GetStorageStats(long storageLimitBytes)
    {
        lock (_lock)
        {
            long fileBytes = 0;
            long fileCount = 0;
            long messageCount = 0;

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT COALESCE(SUM(size), 0) AS bytes, COUNT(*) AS n
                    FROM messages WHERE kind = 'file' AND deleted = 0;
                    """;
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    fileBytes = reader.GetInt64(0);
                    fileCount = reader.GetInt64(1);
                }
            }

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM messages;";
                var val = cmd.ExecuteScalar();
                if (val != null && val is not DBNull)
                {
                    messageCount = Convert.ToInt64(val);
                }
            }

            return new StorageStatsResult(
                FileBytes: fileBytes,
                FileCount: fileCount,
                MessageCount: messageCount,
                LimitBytes: storageLimitBytes,
                OverLimit: fileBytes > storageLimitBytes
            );
        }
    }

    public CleanupResult TombstoneFileMessagesBefore(long cutoff)
    {
        lock (_lock)
        {
            var rowsToTombstone = new List<(long Id, string? StoredName, long Size)>();

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT id, stored_name, size
                    FROM messages
                    WHERE kind = 'file' AND deleted = 0 AND created_at < @cutoff;
                    """;
                cmd.Parameters.AddWithValue("@cutoff", cutoff);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    var storedName = reader.IsDBNull(1) ? null : reader.GetString(1);
                    var size = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
                    rowsToTombstone.Add((id, storedName, size));
                }
            }

            long freedBytes = 0;
            using var updateCmd = _connection.CreateCommand();
            updateCmd.CommandText = "UPDATE messages SET deleted = 1 WHERE id = @id;";
            var idParam = updateCmd.Parameters.Add("@id", SqliteType.Integer);

            foreach (var row in rowsToTombstone)
            {
                if (!string.IsNullOrEmpty(row.StoredName))
                {
                    var fp = Path.Combine(_filesDir, row.StoredName);
                    try
                    {
                        if (File.Exists(fp))
                        {
                            File.Delete(fp);
                            freedBytes += row.Size;
                        }
                    }
                    catch
                    {
                        // Ignore deletion errors matching server.js
                    }
                }

                idParam.Value = row.Id;
                updateCmd.ExecuteNonQuery();
            }

            return new CleanupResult(rowsToTombstone.Count, freedBytes);
        }
    }

    public DeleteMessagesResult DeleteMessages(IEnumerable<long> ids, bool deleteFiles)
    {
        lock (_lock)
        {
            var deleted = 0;
            var filesDeleted = 0;
            var filesKept = 0;
            var freedBytes = 0L;
            var touchedFiles = false;
            var touchedPins = false;

            using var delCmd = _connection.CreateCommand();
            delCmd.CommandText = "DELETE FROM messages WHERE id = @id;";
            var delIdParam = delCmd.Parameters.Add("@id", SqliteType.Integer);

            foreach (var id in ids)
            {
                var row = GetMessage(id);
                if (row == null) continue;

                if (row.PinnedAt != null) touchedPins = true;

                if (row.Kind == "file" && !string.IsNullOrEmpty(row.StoredName))
                {
                    touchedFiles = true;
                    var fp = Path.Combine(_filesDir, row.StoredName);
                    if (deleteFiles)
                    {
                        try
                        {
                            if (File.Exists(fp))
                            {
                                File.Delete(fp);
                                freedBytes += row.Size ?? 0;
                                filesDeleted++;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"delete file failed: {ex.Message}");
                        }
                    }
                    else if (KeepFile(fp, row.Filename))
                    {
                        filesKept++;
                    }
                }

                delIdParam.Value = id;
                delCmd.ExecuteNonQuery();
                deleted++;
            }

            return new DeleteMessagesResult(deleted, filesDeleted, filesKept, freedBytes, touchedPins, touchedFiles);
        }
    }

    private bool KeepFile(string fromPath, string? originalName)
    {
        if (!File.Exists(fromPath)) return false;
        try
        {
            Directory.CreateDirectory(_keptDir);
            var baseName = Path.GetFileName(string.IsNullOrEmpty(originalName) ? Path.GetFileName(fromPath) : originalName);
            var ext = Path.GetExtension(baseName);
            var stem = baseName.Length > ext.Length ? baseName[..^ext.Length] : baseName;
            if (string.IsNullOrEmpty(stem)) stem = "file";
            var target = Path.Combine(_keptDir, baseName);
            for (var n = 2; File.Exists(target); n++)
            {
                target = Path.Combine(_keptDir, $"{stem} ({n}){ext}");
            }
            File.Move(fromPath, target);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"keep file failed: {ex.Message}");
            return false;
        }
    }

    private static FerryMessageRecord ReadRecord(SqliteDataReader reader)
    {
        var id = reader.GetInt64(0);
        var kind = reader.GetString(1);
        var senderId = reader.GetString(2);
        var senderName = reader.GetString(3);
        var text = reader.IsDBNull(4) ? null : reader.GetString(4);
        var filename = reader.IsDBNull(5) ? null : reader.GetString(5);
        var storedName = reader.IsDBNull(6) ? null : reader.GetString(6);
        var size = reader.IsDBNull(7) ? (long?)null : reader.GetInt64(7);
        var deleted = reader.GetInt32(8) != 0;
        var pinnedAt = reader.IsDBNull(9) ? (long?)null : reader.GetInt64(9);
        var createdAt = reader.GetInt64(10);

        return new FerryMessageRecord(
            id,
            kind,
            senderId,
            senderName,
            text,
            filename,
            storedName,
            size,
            deleted,
            pinnedAt,
            createdAt
        );
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _connection.Dispose();
        }
    }
}
