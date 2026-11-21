using System.Text.Json;
using System.Text.Json.Serialization;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Util;

namespace MedalSync.Platform;

internal sealed record CloudJournal(int Format, LibraryIdentity Identity, OwnershipRecord Journal, IReadOnlyList<HandoffRequest> Requests,
    string StorageFolderId, string CaptureFolder, string HeadManifestHash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RetentionHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MigrationDigest = null);
internal sealed record CloudJournalVersion(CloudJournal Value, string Version);

internal interface ICloudJournal
{
    bool HasMd5 { get; }
    bool Exists();
    CloudJournalVersion Read();
    bool CompareExchange(CloudJournalVersion expected, CloudJournal next);
    void Create(CloudJournal initial, string parentId);
}

internal static class CloudJournalProvider
{
    internal static void Validate(CloudJournal value)
    {
        if (value.Format != 1 || value.HeadManifestHash is not { Length: 64 } || !value.HeadManifestHash.All(char.IsAsciiHexDigit)
            || value.RetentionHash is { } retention && (retention.Length != 64 || !retention.All(char.IsAsciiHexDigit))
            || value.MigrationDigest is { } migration && (migration.Length != 64 || !migration.All(char.IsAsciiHexDigit))
            || string.IsNullOrWhiteSpace(value.StorageFolderId) || !WindowsPath.TryParse(value.CaptureFolder, out _, out _)
            || value.Identity is not { Format: LibraryIdentity.CurrentFormat } identity
            || !CoordinationStore.IsValidId(identity.LibraryId) || string.IsNullOrEmpty(identity.AccountId)
            || value.Journal is not { Format: OwnershipRecord.CurrentFormat } journal || journal.LibraryId != identity.LibraryId
            || journal.AccountId != identity.AccountId || !Enum.IsDefined(journal.State) || journal.Revision < 1 || journal.CommittedGeneration < 0
            || (journal.State == OwnershipState.Clean ? journal.Owner is not null || journal.SessionId is not null
                : journal.Owner is null || !CoordinationStore.IsValidId(journal.Owner.DeviceId) || !CoordinationStore.IsValidId(journal.SessionId))
            || journal.State == OwnershipState.Maintenance && (journal.MaintenanceExpiresAt is null || string.IsNullOrWhiteSpace(journal.Reason)
                || journal.OperationDigest is not { Length: 64 } || !journal.OperationDigest.All(char.IsAsciiHexDigit))
            || (journal.State is OwnershipState.Migrating or OwnershipState.Migrated) && (journal.OperationDigest is not { Length: 64 }
                || !journal.OperationDigest.All(char.IsAsciiHexDigit))
            || value.Requests is null || value.Requests.Count > 32 || value.Requests.Any(r => r is null || r.Format != 1
                || r.Device is null || !CoordinationStore.IsValidId(r.Device.DeviceId) || !CoordinationStore.IsValidId(r.RequestId))
            || value.Requests.Select(r => r.Device.DeviceId).Distinct(StringComparer.Ordinal).Count() != value.Requests.Count)
            throw new InvalidDataException("The cloud ownership journal is incomplete or belongs to another format.");
    }

    internal static ICloudJournal For(MedalSyncConfig config, Action? requireTime = null)
    {
        string id = config.CloudJournalId!;
        if (!id.Contains('.')) return GoogleDriveJournal.For(config, requireTime);

        var reservation = ParseId(id);
        var credentials = new DriveAccessToken(config.ExpandedRclonePath, config.Remote, config.Replication!.RcloneConfigPath!,
            requireTime, expectedType: reservation.Provider);
        var files = new JournalReservations(config.ExpandedRclonePath, config.Remote, config.Replication.RcloneConfigPath!, requireTime);
        return new FileCloudJournal(reservation.ItemId, reservation.FolderId,
            reservation.Provider == "onedrive"
                ? new OneDriveJournalFiles(credentials.Get, () => GraphEndpoint(credentials.Settings()), requireTime)
                : new DropboxJournalFiles(credentials.Get, requireTime),
            () => files.Reserve(reservation.Provider, credentials.Settings(), probe: true));
    }

    internal static string Reserve(string rclone, string remote, string config)
    {
        var settings = new DriveAccessToken(rclone, remote, config).Settings();
        string provider = settings.GetProperty("type").GetString()!;
        if (provider == "drive") return GoogleDriveJournal.GenerateId(rclone, remote, config);
        if (provider == "onedrive") _ = GraphEndpoint(settings);
        var reserved = new JournalReservations(rclone, remote, config, null).Reserve(provider, settings, probe: false);
        var values = new[] { reserved.ItemId, reserved.FolderId };
        return provider + "." + Convert.ToBase64String(Json.Serialize(values)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static void RequireConfiguredProvider(MedalSyncConfig config)
    {
        string id = config.CloudJournalId!;
        string type = id.Contains('.') ? ParseId(id).Provider : "drive";
        var settings = new DriveAccessToken(config.ExpandedRclonePath, config.Remote, config.Replication!.RcloneConfigPath!).Settings();
        if (settings.GetProperty("type").GetString() != type)
            throw new UnsafeOperationException("The cloud journal ID and rclone provider do not match. Use the journal ID allocated for this remote.");
        if (type == "onedrive") _ = GraphEndpoint(settings);
    }

    private static (string Provider, string ItemId, string FolderId) ParseId(string id)
    {
        if (!MedalSyncConfig.ValidCloudJournalId(id)) throw new InvalidDataException("Invalid cloud journal ID.");
        string[] parts = id.Split('.');
        try
        {
            string base64 = parts[1].Replace('-', '+').Replace('_', '/');
            byte[] bytes = Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
            string[] values = Json.Deserialize<string[]>(bytes, "Cloud journal ID");
            if (values.Length != 2 || values.Any(string.IsNullOrWhiteSpace)) throw new FormatException();
            return (parts[0], values[0], values[1]);
        }
        catch (FormatException) { throw new InvalidDataException("Invalid cloud journal ID. Obtain it with 'storage cloud-id'."); }
    }

    private static string GraphEndpoint(JsonElement settings)
    {
        if (settings.TryGetProperty("tenant_url", out var tenant) && !string.IsNullOrWhiteSpace(tenant.GetString()))
            throw new UnsafeOperationException("Use a Microsoft Graph OneDrive remote for the cloud journal.");
        return (settings.TryGetProperty("region", out var region) ? region.GetString() : "") switch
        {
            null or "" or "global" => "https://graph.microsoft.com",
            "us" => "https://graph.microsoft.us",
            "usl4" => "https://graph.microsoft.us",
            "usl5" => "https://dod-graph.microsoft.us",
            "cn" => "https://microsoftgraph.chinacloudapi.cn",
            _ => throw new UnsafeOperationException("This OneDrive region is unsupported for cloud coordination."),
        };
    }

    private sealed class JournalReservations(string rclone, string remote, string config, Action? requireTime)
    {
        private string Run(params string[] args) => new RcloneRunner(rclone).Run(
            [.. args, "--config", Environment.ExpandEnvironmentVariables(config), "--retries", "1", "--low-level-retries", "1", "--contimeout", "10s"],
            TimeSpan.FromSeconds(30), requireTime, captureOutput: true);

        public (string ItemId, string FolderId, Action Cleanup) Reserve(string provider, JsonElement settings, bool probe)
        {
            string folder = remote.TrimEnd('/');
            Run("mkdir", folder);
            string folderId = Id(folder, directory: true);
            string path = folder + "/medal-sync-" + (probe ? "probe-" : "journal-") + Guid.NewGuid().ToString("N") + ".json";
            string local = Path.Combine(Path.GetTempPath(), "medal-sync-" + Guid.NewGuid().ToString("N") + ".json");
            AtomicFile.CreateNew(local, FileCloudJournal.ReservedBytes);
            try
            {
                Run("copyto", local, path, "--immutable");
                string id = Id(path, directory: false);
                if (provider == "onedrive" && !id.Contains('#'))
                {
                    if (!settings.TryGetProperty("drive_id", out var drive) || string.IsNullOrWhiteSpace(drive.GetString()))
                        throw new InvalidDataException("The OneDrive remote did not identify its drive.");
                    id = drive.GetString() + "#" + id;
                }
                return (id, folderId, () => Run("deletefile", path));
            }
            finally { File.Delete(local); }
        }

        private string Id(string path, bool directory)
        {
            using var listing = JsonDocument.Parse(Run("lsjson", path, "--stat", "--no-mimetype"));
            var item = listing.RootElement;
            if (!item.TryGetProperty("IsDir", out var isDir) || isDir.GetBoolean() != directory
                || !item.TryGetProperty("ID", out var id) || string.IsNullOrWhiteSpace(id.GetString()))
                throw new InvalidDataException("The cloud provider did not identify the dedicated storage folder or reserved journal file.");
            return id.GetString()!;
        }
    }
}
