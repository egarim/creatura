using Microsoft.Data.Sqlite;

namespace Creatura.Cells.Hub;

/// <summary>What happened to an upload.</summary>
public enum FilePut { Stored, AlreadyHeld, Altered, TooLarge }

/// <summary>
/// The hub's shared files (docs/design-shared-files.md): ciphertext only, one owner-only file per SHA-256 under
/// <see cref="Directory"/>, with a row per file in the hub's database. An upload is kept only when its bytes hash to
/// its name (F5) and fit the limit (F6). A file is deleted when its retention runs out (F7); its row stays, so a fetch
/// can say "expired" rather than "never had it".
/// </summary>
public sealed class HubFileStore
{
    private readonly string _database;
    private readonly TimeProvider _clock;
    private readonly object _write = new();

    public HubFileStore(string database, string directory, long maxBytes, TimeSpan retention, TimeProvider clock)
    {
        _database = database;
        Directory = Path.GetFullPath(directory);
        MaxBytes = maxBytes;
        Retention = retention;
        _clock = clock;
        OwnerOnly.CreateDirectory(Directory);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS files (hash TEXT PRIMARY KEY, size INTEGER NOT NULL, uploaded INTEGER NOT NULL, cell TEXT, expired INTEGER NOT NULL DEFAULT 0)";
        command.ExecuteNonQuery();
    }

    public string Directory { get; }

    public long MaxBytes { get; }

    public TimeSpan Retention { get; }

    public FilePut Put(string hash, byte[] bytes, string? cell)
    {
        if (!SharedFiles.IsHash(hash) || SharedFiles.Hash(bytes) != hash) return FilePut.Altered;
        if (bytes.Length > MaxBytes) return FilePut.TooLarge;
        lock (_write)
        {
            Sweep();
            var path = Path.Combine(Directory, hash);
            if (File.Exists(path)) return FilePut.AlreadyHeld;
            OwnerOnly.Replace(path, bytes);
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO files (hash, size, uploaded, cell, expired) VALUES ($hash, $size, $now, $cell, 0) ON CONFLICT (hash) DO UPDATE SET size = excluded.size, uploaded = excluded.uploaded, cell = excluded.cell, expired = 0";
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$size", bytes.LongLength);
            command.Parameters.AddWithValue("$now", _clock.GetUtcNow().ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$cell", (object?)cell ?? DBNull.Value);
            command.ExecuteNonQuery();
            return FilePut.Stored;
        }
    }

    /// <summary>The file's bytes; null with <paramref name="expired"/> telling an expired file from one never held.</summary>
    public byte[]? Get(string hash, out bool expired)
    {
        expired = false;
        if (!SharedFiles.IsHash(hash)) return null;
        lock (_write) Sweep();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT expired FROM files WHERE hash = $hash";
        command.Parameters.AddWithValue("$hash", hash);
        var row = command.ExecuteScalar();
        if (row is null) return null;
        if ((long)row == 1)
        {
            expired = true;
            return null;
        }
        var path = Path.Combine(Directory, hash);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>Deletes the files past their retention, keeping their rows marked expired.</summary>
    public int Sweep()
    {
        using var connection = Open();
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT hash FROM files WHERE expired = 0 AND uploaded < $cutoff";
        select.Parameters.AddWithValue("$cutoff", (_clock.GetUtcNow() - Retention).ToUnixTimeMilliseconds());
        var due = new List<string>();
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read()) due.Add(reader.GetString(0));
        }
        foreach (var hash in due)
        {
            File.Delete(Path.Combine(Directory, hash));
            using var mark = connection.CreateCommand();
            mark.CommandText = "UPDATE files SET expired = 1 WHERE hash = $hash";
            mark.Parameters.AddWithValue("$hash", hash);
            mark.ExecuteNonQuery();
        }
        return due.Count;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;";
        command.ExecuteNonQuery();
        return connection;
    }
}
