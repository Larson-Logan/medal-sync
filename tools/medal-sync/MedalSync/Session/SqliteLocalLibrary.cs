using System.Text.Json;
using MedalSync.Library;

namespace MedalSync.Session;

/// <summary>The local Medal database, read and restored through the library adapter.</summary>
internal sealed class SqliteLocalLibrary(string databasePath, string accountId, string backupDirectory, IClock clock,
    string? cloudCaptureFolder = null) : ILocalLibrary
{
    public string AccountId { get; } = accountId;

    public LibrarySnapshot Read() => LibraryDatabase.ReadSnapshot(databasePath, AccountId);

    public RestoreResult Restore(LibrarySnapshot snapshot, bool allowEmptyReplacement = false)
    {
        var result = LibraryRestorer.Restore(databasePath, snapshot, new RestoreOptions(AccountId, backupDirectory, AllowEmptyReplacement: allowEmptyReplacement), clock.Now);
        if (cloudCaptureFolder is not null && CaptureFolder() is { } current
            && !Util.WindowsPath.Parse(current).Equals(Util.WindowsPath.Parse(cloudCaptureFolder)))
        {
            if (!result.Changed)
            {
                using var connection = LibraryDatabase.OpenReadOnly(databasePath);
                DatabaseBackup.Create(connection, backupDirectory, AccountId, clock.Now);
            }
            CaptureFolderSetting.Change(databasePath, current, cloudCaptureFolder);
        }
        return result;
    }

    public string? CaptureFolder()
    {
        using var connection = LibraryDatabase.OpenReadOnly(databasePath);
        string? json = LibraryDatabase.ReadSetting(connection, null, "ClipFolder");
        if (json is null) return null;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() : null;
    }
}
