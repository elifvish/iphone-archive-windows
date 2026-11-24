using IphoneArchive.Models;
using Microsoft.Data.Sqlite;

namespace IphoneArchive.Services;

public sealed class ManifestStore : IDisposable
{
    private readonly string _destinationRoot;
    private readonly SqliteConnection _connection;

    public ManifestStore(string destinationRoot)
    {
        _destinationRoot = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(_destinationRoot);
        var databasePath = Path.Combine(_destinationRoot, ".iphone-export.db");
        _connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWriteCreate;Cache=Shared");
        _connection.Open();

        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            CREATE TABLE IF NOT EXISTS transfers (
                source_key TEXT PRIMARY KEY,
                original_name TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                output_size_bytes INTEGER NOT NULL,
                sha256 TEXT NOT NULL,
                capture_utc TEXT NOT NULL,
                completed_utc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        try
        {
            using var migration = _connection.CreateCommand();
            migration.CommandText = "ALTER TABLE transfers ADD COLUMN output_size_bytes INTEGER NOT NULL DEFAULT 0";
            migration.ExecuteNonQuery();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1)
        {
            // The column already exists.
        }

        using (var backfill = _connection.CreateCommand())
        {
            backfill.CommandText = "UPDATE transfers SET output_size_bytes = size_bytes WHERE output_size_bytes = 0";
            backfill.ExecuteNonQuery();
        }

        try
        {
            File.SetAttributes(databasePath, File.GetAttributes(databasePath) | FileAttributes.Hidden);
        }
        catch
        {
            // A destination such as a NAS may not support the Hidden attribute.
        }
    }

    public bool IsCompleted(string sourceKey, ulong expectedSize, out string? relativePath)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT relative_path, size_bytes, output_size_bytes FROM transfers WHERE source_key = $key LIMIT 1";
        command.Parameters.AddWithValue("$key", sourceKey);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            relativePath = null;
            return false;
        }

        relativePath = reader.GetString(0);
        var sourceSize = reader.GetInt64(1);
        var outputSize = reader.GetInt64(2);
        var fullPath = Path.Combine(_destinationRoot, relativePath);
        return File.Exists(fullPath)
               && sourceSize >= 0
               && outputSize > 0
               && (ulong)sourceSize == expectedSize
               && new FileInfo(fullPath).Length == outputSize;
    }

    public void MarkCompleted(PortableMediaItem item, string sourceKey, string fullPath, string sha256)
    {
        var relativePath = Path.GetRelativePath(_destinationRoot, fullPath);
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO transfers(source_key, original_name, relative_path, size_bytes, output_size_bytes, sha256, capture_utc, completed_utc)
            VALUES($key, $name, $path, $size, $outputSize, $hash, $capture, $completed)
            ON CONFLICT(source_key) DO UPDATE SET
                original_name = excluded.original_name,
                relative_path = excluded.relative_path,
                size_bytes = excluded.size_bytes,
                output_size_bytes = excluded.output_size_bytes,
                sha256 = excluded.sha256,
                capture_utc = excluded.capture_utc,
                completed_utc = excluded.completed_utc;
            """;
        command.Parameters.AddWithValue("$key", sourceKey);
        command.Parameters.AddWithValue("$name", item.OriginalFileName);
        command.Parameters.AddWithValue("$path", relativePath);
        command.Parameters.AddWithValue("$size", checked((long)item.Size));
        command.Parameters.AddWithValue("$outputSize", new FileInfo(fullPath).Length);
        command.Parameters.AddWithValue("$hash", sha256);
        command.Parameters.AddWithValue("$capture", item.CaptureTime.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$completed", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}

