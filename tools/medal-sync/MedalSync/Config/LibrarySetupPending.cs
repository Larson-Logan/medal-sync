using MedalSync.Util;

namespace MedalSync.Config;

/// <summary>Local setup intent only. Never evidence of ownership or a completed enrollment.</summary>
internal sealed record LibrarySetupPending(int Format, string LibraryId, string AccountId, bool NewLibrary)
{
    private static string PathFor(LocalState state) => Path.Combine(state.Root, "library-setup.json");

    public static LibrarySetupPending? Read(LocalState state, MedalSyncConfig config)
    {
        if (!File.Exists(PathFor(state))) return null;
        var pending = Json.ReadFile<LibrarySetupPending>(PathFor(state), "Pending library setup");
        if (pending.Format != 1 || pending.LibraryId != config.LibraryId || pending.AccountId != config.AccountId)
            throw new InvalidDataException("Pending setup belongs to another configuration. Keep the setup record and recover the original configuration.");
        return pending;
    }

    public static LibrarySetupPending Save(LocalState state, MedalSyncConfig config, bool newLibrary)
    {
        var pending = new LibrarySetupPending(1, config.LibraryId, config.AccountId, newLibrary);
        AtomicFile.CreateNew(PathFor(state), Json.Serialize(pending));
        return pending;
    }

    public void Complete(LocalState state, MedalSyncConfig config)
    {
        if (Read(state, config) != this) throw new UnsafeOperationException("Pending library setup changed; keep its evidence and reopen configuration.");
        File.Delete(PathFor(state));
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Action => NewLibrary ? "create" : "join";

    public static bool HasSession(LocalState state) => Directory.Exists(Path.Combine(state.Root, "sessions"))
        && Directory.EnumerateFiles(Path.Combine(state.Root, "sessions"), "*.json").Any();
}
