using System.Text;
using MedalSync.Config;
using MedalSync.Util;

namespace MedalSync.Cli;

/// <summary>Portable configuration preparation; never opens Medal or a share.</summary>
internal static class ConfigCommands
{
    public static int Validate(Arguments args, TextWriter output)
    {
        string path = args.Required("--config");
        args.RequireAllUsed();
        var config = MedalSyncConfig.Load(path);
        output.WriteLine($"Valid configuration: account {config.AccountId}, library {config.LibraryId}.");
        output.WriteLine($"Capture: {config.Capture.Display}; primary storage: {config.PrimaryCapture}{(config.PrimaryIsRclone ? " (rclone remote)" : "")}; cache: {config.CacheRoot}.");
        var targets = config.Replication?.Targets ?? [];
        output.WriteLine($"Recording storage: {config.MountRemote}; cloud replicas: {targets.Count} ({targets.Count(t => t.Required)} required, others retry when offline).");
        output.WriteLine("Syntax and path checks only; storage access, sync exclusions and device readiness are separate checks.");
        return 0;
    }

    public static int Create(Arguments args, TextWriter output)
    {
        string account = args.Required("--account-id");
        string cache = args.Required("--cache-root");
        string remote = args.Required("--remote");
        string mode = args.Value("--storage-mode") ?? "server";
        if (mode is not ("server" or "local")) throw new UsageException("--storage-mode must be server or local.");
        string coordination = args.Value("--coordination-root") ?? (mode == "local"
            ? Path.Combine(LocalState.ForCurrentUser().Root, "coordination") : throw new UsageException("--coordination-root is required for server mode."));
        string target = Path.GetFullPath(args.Required("--out"));
        string? library = args.Value("--library-id");
        bool fresh = args.Flag("--new-library");
        bool spike = args.Flag("--spike");
        bool apply = args.Flag("--apply");
        string capture = args.Value("--capture-folder") ?? @"M:\Clips";
        string? rclone = args.Value("--rclone-path");
        string? localCopy = args.Value("--local-copy-root");
        string? cloudConfig = args.Value("--rclone-config");
        string? cloudRemote = args.Value("--cloud-remote");
        string conflicts = args.Value("--replica-conflicts") ?? "stop";
        if (conflicts is not ("stop" or "version")) throw new UsageException("--replica-conflicts must be stop or version.");
        string? folder = args.Value("--sync-folder");
        string pattern = args.Value("--sync-pattern") ?? "/Videos/Clips";
        args.RequireAllUsed();
        if (fresh == (library is not null)) throw new UsageException("Choose exactly one of --new-library or --library-id ID (reuse the existing ID on another PC).");
        if (string.IsNullOrWhiteSpace(account) || !account.All(char.IsAsciiDigit) || account.All(c => c == '0'))
            throw new UsageException("--account-id must be the real numeric Medal account ID from 'inspect'.");
        if (File.Exists(target)) throw new UnsafeOperationException($"'{target}' already exists. Validate and reuse it; configuration creation never replaces an existing identity.");
        if (!Directory.Exists(Path.GetDirectoryName(target))) throw new UnsafeOperationException("The output parent directory must already exist.");
        var config = new MedalSyncConfig(1, library ?? Guid.NewGuid().ToString("N"), account, coordination, remote, capture, cache,
            rclone ?? DefaultRclonePath(),
            new MountConfig(5, 20, 20, 300, 30, 1000, 2, spike ? 53683 : 53682), 4,
            spike || folder is null ? [] : [new SyncExclusion(folder, pattern)])
        {
            StorageMode = mode == "local" ? StorageMode.Local : StorageMode.Server,
            Replication = localCopy is null && cloudRemote is null && cloudConfig is null && conflicts == "stop" ? null
                : new ReplicationConfig(localCopy, cloudConfig, cloudRemote is null ? [] : [new ReplicaTarget("cloud", cloudRemote)])
                    { ConflictPolicy = conflicts == "version" ? ReplicaConflictPolicy.Version : ReplicaConflictPolicy.Stop },
        };
        config.Validate();
        if (spike && !config.RemoteRoot.Segments.LastOrDefault("").Contains("spike", StringComparison.OrdinalIgnoreCase))
            throw new UsageException("A spike remote must end in a folder whose name contains 'spike'.");
        byte[] bytes = Json.Serialize(config);
        if (!apply)
        {
            output.WriteLine(Encoding.UTF8.GetString(bytes));
            output.WriteLine($"Preview only; nothing written. Add --apply to create '{target}'.");
            if (fresh) output.WriteLine("A new library ID is generated at apply time.");
            return 0;
        }
        AtomicFile.CreateNew(target, bytes);
        output.WriteLine($"Created '{target}' for library {config.LibraryId}. No Medal, startup, server or sync settings changed.");
        return 0;
    }

    internal static string DefaultRclonePath() => File.Exists(Path.Combine(AppContext.BaseDirectory, "rclone.exe"))
        ? Path.Combine(AppContext.BaseDirectory, "rclone.exe") : Path.Combine(LocalState.ForCurrentUser().Root, "bin", "rclone.exe");
}
