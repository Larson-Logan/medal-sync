using Microsoft.Data.Sqlite;

namespace LarsonServer.MedalSync.Library;

/// <summary>
/// Renders JSON exactly the way SQLite's <c>json()</c> does, so edited metadata
/// compares byte-for-byte with what the database will read back.
/// </summary>
internal static class SqliteJson
{
    public static string Canonical(string json)
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json(jsonb($value))";
        command.Parameters.AddWithValue("$value", json);
        return command.ExecuteScalar() as string
            ?? throw new InvalidDataException("SQLite could not render the JSON value.");
    }
}
