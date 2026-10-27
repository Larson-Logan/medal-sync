using System.Runtime.Versioning;
using MedalSync.Config;
using MedalSync.Platform;

namespace MedalSync.Cli;

internal static class SetupCommands
{
    [SupportedOSPlatform("windows")]
    internal static int Check(Arguments args, TextWriter output)
    {
        string? report = args.Value("--out");
        string? selectedRoot = args.Value("--state-root");
        args.RequireAllUsed();
        string root = selectedRoot is null ? ManagedStateRoot() : Path.GetFullPath(selectedRoot);
        string message;
        int result;
        try
        {
            SetupMaintenance.RequireReady(root, SetupMaintenance.ApplicationRunning(), SessionCommands.AnotherInstanceRunning());
            message = "Ready for program installation. Existing user settings and library data will be preserved.";
            result = 0;
        }
        catch (Exception ex) when (ex is MedalSyncException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            message = ex.Message;
            result = 1;
        }
        output.WriteLine(message);
        if (report is not null)
        {
            using var stream = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.Write(message);
        }
        return result;
    }

    private static string ManagedStateRoot() => LocalState.ForCurrentUser().Root;
}
