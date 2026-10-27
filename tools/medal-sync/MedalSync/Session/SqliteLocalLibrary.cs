using System.Text.Json;
using LarsonServer.MedalSync.Library;

namespace LarsonServer.MedalSync.Session;

/// <summary>The local Medal database, read and restored through the library adapter.</summary>
internal sealed class SqliteLocalLibrary(string databasePath, string accountId, string backupDirectory, IClock clock) : ILocalLibrary
{
    public string AccountId { get; } = accountId;

    public LibrarySnapshot Read() => LibraryDatabase.ReadSnapshot(databasePath, AccountId);

    public RestoreResult Restore(LibrarySnapshot snapshot) =>
        LibraryRestorer.Restore(databasePath, snapshot, new RestoreOptions(AccountId, backupDirectory), clock.Now);

    public string? CaptureFolder()
    {
        using var connection = LibraryDatabase.OpenReadOnly(databasePath);
        string? json = LibraryDatabase.ReadSetting(connection, null, "ClipFolder");
        if (json is null) return null;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() : null;
    }
}
