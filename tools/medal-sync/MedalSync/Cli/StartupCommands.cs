using System.Runtime.Versioning;
using MedalSync.Config;
using MedalSync.Platform;

namespace MedalSync.Cli;

/// <summary><c>startup [--apply | --restore]</c>: start Medal through Medal Sync at sign-in, or put Medal's own entries back.</summary>
internal static class StartupCommands
{
    [SupportedOSPlatform("windows")]
    public static int Run(Arguments args, TextWriter output)
    {
        bool apply = args.Flag("--apply"), restore = args.Flag("--restore");
        args.RequireAllUsed();
        if (apply && restore) throw new UsageException("Use --apply or --restore, not both.");
        using var startup = StartupIntegration.ForCurrentUser(LocalState.ForCurrentUser().Root,
            Path.Combine(AppContext.BaseDirectory, StartupEntries.LauncherFile));
        return Run(startup, apply, restore, output);
    }

    [SupportedOSPlatform("windows")]
    internal static int Run(StartupIntegration startup, bool apply, bool restore, TextWriter output)
    {
        if (restore)
        {
            foreach (string line in startup.Restore()) output.WriteLine(line);
            output.WriteLine("Medal starts with its own entries again. Open Medal through Medal Sync to use the clip drive.");
            return 0;
        }
        if (apply)
        {
            var done = startup.Apply();
            foreach (string line in done) output.WriteLine(line);
            if (done.Count == 0) output.WriteLine("Already set up; nothing changed.");
            output.WriteLine("Medal now starts through Medal Sync at sign-in. Undo with 'medal-sync startup --restore'.");
            return 0;
        }
        var preview = startup.Preview();
        foreach (string line in preview) output.WriteLine(line);
        output.WriteLine(startup.Enabled
            ? "Preview only; nothing changed. --apply repoints anything Medal restored; --restore puts Medal's own entries back."
            : "Preview only; nothing changed. Add --apply to start Medal through Medal Sync at sign-in (Medal's entries are saved first).");
        return 0;
    }
}
