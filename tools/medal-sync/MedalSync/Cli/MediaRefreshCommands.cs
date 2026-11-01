using System.Runtime.Versioning;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Platform;
using MedalSync.Util;

namespace MedalSync.Cli;

[SupportedOSPlatform("windows")]
internal static class MediaRefreshCommands
{
    public static int Run(Arguments args, TextWriter output)
    {
        bool apply = args.Flag("--apply");
        bool acceptUnknown = args.Flag("--accept-unknown-media");
        string? input = args.Value("--in");
        string? destination = args.Value("--out");
        args.RequireAllUsed();
        if (apply ? input is null || destination is not null : destination is null || input is not null || acceptUnknown)
            throw new UsageException("Use refresh-media --out FILE to review, or refresh-media --in FILE --apply [--accept-unknown-media] to apply that exact review.");
        using var instance = SessionCommands.SingleInstance();
        var state = LocalState.ForCurrentUser();
        string configFileDigest = Hashing.Sha256File(state.ConfigPath);
        var config = MedalSyncConfig.Load(state.ConfigPath);
        var install = MedalEnvironment.Locate();
        if (install.AccountId != config.AccountId) throw new IncompatibleLibraryException("Medal's current account differs from the configured account.");
        var device = state.ExistingDevice();
        var store = CoordinationStore.Open(config);
        var refresh = new PersistentMediaRefresh(config, state, device, store, () =>
        {
            if (Hashing.Sha256File(state.ConfigPath) != configFileDigest || state.ExistingDevice() != device)
                throw new UnsafeOperationException("Configuration or device identity changed during review/refresh. Ownership and recovery evidence are retained; restore the reviewed settings before retrying.");
            if (new MedalProcess(install).IsRunning() || Directory.Exists(config.DriveRoot.Display))
                throw new UnsafeOperationException("Quit Medal normally and finish its mounted session before refreshing persistent media.");
        }, new MedalCompatibilityGuard(install).RequireLaunch);
        if (!apply)
        {
            var plan = refresh.Preview();
            foreach (var file in plan.Files.Where(f => f.Conflict)) output.WriteLine($"{file.Path}: {file.Classification}");
            AtomicFile.CreateNew(destination!, Json.Serialize(plan));
            output.WriteLine($"Review saved to {destination}. Contains private paths/identities: keep it local. Review every conflict before applying; old local bytes will be preserved.");
        }
        else
        {
            var plan = Json.ReadFile<MediaRefreshPlan>(input!, "Persistent media review");
            string recovery = refresh.Apply(plan, acceptUnknown);
            output.WriteLine($"Persistent media verified. Previous local bytes and review are retained at {recovery}. Open Medal through Medal Sync normally.");
        }
        return 0;
    }
}
