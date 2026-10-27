using Microsoft.Data.Sqlite;

namespace MedalSync.Tests.Fixtures;

/// <summary>
/// Disposable synthetic Medal libraries built from the real schema (no real
/// rows). Every path and ID here is invented.
/// </summary>
internal sealed class MedalFixture : IDisposable
{
    public const string AccountId = "12345";

    public MedalFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "medal-sync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public static string SchemaSql =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "medal-schema-v4.sql"));

    public string PathFor(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>A new empty library with synthetic device settings for <paramref name="machine"/>.</summary>
    public string CreateLibrary(string machine, string? name = null)
    {
        string directory = PathFor(machine);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name ?? $"medal-{AccountId}.db");
        using var connection = Open(path);
        Execute(connection, "PRAGMA journal_mode = WAL");
        Execute(connection, SchemaSql);
        SetSetting(connection, "ClipFolder", "\"M:\\\\Clips\\\\\"");
        SetSetting(connection, "SelectedMicDevice", $"{{\"device\":\"{machine}-microphone\"}}");
        SetSetting(connection, "EncoderOptions", $"{{\"gpu\":\"{machine}-gpu\"}}");
        SetSetting(connection, "HardwareId", $"\"{machine}-hardware\"");
        return path;
    }

    public static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    public static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public static void SetSetting(SqliteConnection connection, string key, string json) =>
        Execute(connection, "INSERT OR REPLACE INTO key_values (key, value) VALUES ($k, jsonb($v))", ("$k", key), ("$v", json));

    public static void AddContent(string database, string id, string? video = null, string? thumbnail = null,
        string metadata = "{\"title\":\"Synthetic clip\",\"tags\":[],\"isFavorited\":false}", string? remoteId = null, long createdAt = 1_700_000_000_000)
    {
        using var connection = Open(database);
        Execute(connection,
            "INSERT INTO contents (created_at, category_id, video_path, image_path, thumbnail_path, metadata, remote_content_id, local_content_id, parent_id, film_reel_path) "
            + "VALUES ($created, 'game-1', $video, NULL, $thumb, jsonb($meta), $remote, $id, NULL, NULL)",
            ("$created", createdAt), ("$video", video), ("$thumb", thumbnail), ("$meta", metadata), ("$remote", remoteId), ("$id", id));
    }

    public static void DeleteContent(string database, string id, long deletedAt = 1_700_000_100_000)
    {
        using var connection = Open(database);
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO deleted_contents (local_content_id, deleted_at, blob) "
                + "SELECT local_content_id, $at, jsonb(json_object('local_content_id', local_content_id)) FROM contents WHERE local_content_id = $id; "
                + "DELETE FROM contents WHERE local_content_id = $id;";
            command.Parameters.AddWithValue("$at", deletedAt);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public static string Settings(string database)
    {
        using var connection = Open(database);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(key || '=' || json(value), ';') FROM (SELECT key, value FROM key_values ORDER BY key)";
        return (string)command.ExecuteScalar()!;
    }

    public static long Count(string database, string key)
    {
        using var connection = Open(database);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count FROM content_counts WHERE key = $k";
        command.Parameters.AddWithValue("$k", key);
        return (long)command.ExecuteScalar()!;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Leave the temporary folder if the OS still holds a handle.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
