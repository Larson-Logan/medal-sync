using System.Text.Json;
using MedalSync.Util;

namespace MedalSync.Library;

/// <summary>One intentional device-setting change after a verified enrollment.
/// The restorer has already backed up the full DB and preserved all settings.</summary>
internal static class CaptureFolderSetting
{
    public static void Change(string database, string expected, string destination)
    {
        using var connection = LibraryDatabase.OpenReadWrite(database);
        using var transaction = connection.BeginTransaction(deferred: false);
        string? current = JsonSerializer.Deserialize<string>(LibraryDatabase.ReadSetting(connection, transaction, "ClipFolder") ?? "null");
        if (current is null || !WindowsPath.Parse(current).Equals(WindowsPath.Parse(expected)))
            throw new UnsafeOperationException("The capture folder changed since enrollment was reviewed.");
        var before = OtherSettings();
        using (var command = MedalSchema.Command(connection, transaction, "UPDATE key_values SET value=jsonb($path) WHERE key='ClipFolder'"))
        {
            command.Parameters.AddWithValue("$path", JsonSerializer.Serialize(destination + "\\"));
            if (command.ExecuteNonQuery() != 1) throw new UnsafeOperationException("Expected exactly one capture folder setting.");
        }
        if (!before.SequenceEqual(OtherSettings())) throw new UnsafeOperationException("Another device setting changed; capture setting transaction was rolled back.");
        string? readback = JsonSerializer.Deserialize<string>(LibraryDatabase.ReadSetting(connection, transaction, "ClipFolder") ?? "null");
        if (readback is null || !WindowsPath.Parse(readback).Equals(WindowsPath.Parse(destination)))
            throw new UnsafeOperationException("Capture folder readback failed.");
        transaction.Commit();

        List<string> OtherSettings()
        {
            using var command = MedalSchema.Command(connection, transaction, "SELECT key, hex(value), typeof(value) FROM key_values WHERE key <> 'ClipFolder' ORDER BY key");
            using var reader = command.ExecuteReader();
            var result = new List<string>();
            while (reader.Read()) result.Add(reader.GetString(0) + ":" + reader.GetString(1) + ":" + reader.GetString(2));
            return result;
        }
    }
}
