using System.Text.RegularExpressions;
using MedalSync.Config;
using MedalSync.Util;

namespace MedalSync.Platform;

internal enum CloudProvider { GoogleDrive, OneDrive, Dropbox }

/// <param name="ConfigPath">The credential file that now holds the sign-in; null when nothing was changed.</param>
internal sealed record SignInResult(bool Succeeded, string Message, string? ConfigPath = null);

/// <summary>
/// Creates or reconnects one rclone remote with the bundled rclone, which opens the provider's sign-in page in the
/// default browser and writes the token itself; Medal Sync never reads, shows or logs it. rclone works on a private
/// copy of the credential file, and the result replaces the real file only after the existing read-only storage check
/// passes for that remote, so a cancelled, failed or unverified sign-in leaves the credential file exactly as it was.
/// </summary>
internal sealed partial class ProviderSignIn(string rclonePath, LocalState state,
    Func<IReadOnlyList<string>, CancellationToken, string>? run = null,
    Func<string, string, CancellationToken, (StorageCheckResult Result, string Detail)>? check = null)
{
    /// <summary>How long the user has to finish signing in on the provider's page.</summary>
    internal static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(10);

    public const string GoogleClientGuide = "https://rclone.org/drive/#making-your-own-client-id";

    public const string GoogleTestingWarning = "If you use your own Google OAuth client, publish it: a client left in Testing status "
        + "expires every sign-in after seven days.";

    public static string RcloneType(CloudProvider provider) => provider switch
    {
        CloudProvider.GoogleDrive => "drive",
        CloudProvider.OneDrive => "onedrive",
        CloudProvider.Dropbox => "dropbox",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    public static CloudProvider? FromType(string? type) => type switch
    {
        "drive" => CloudProvider.GoogleDrive,
        "onedrive" => CloudProvider.OneDrive,
        "dropbox" => CloudProvider.Dropbox,
        _ => null,
    };

    /// <summary>A credential file in Medal Sync's private state directory, used when none is configured yet.</summary>
    public static string DefaultConfigPath(LocalState state) => Path.Combine(state.Root, "rclone", "rclone.conf");

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex RemoteName();

    public static bool ValidRemoteName(string name) => RemoteName().IsMatch(name);

    /// <summary>The remote name in an rclone path such as <c>gdrive:MedalSync</c>, or null.</summary>
    public static string? RemoteOf(string path)
    {
        int colon = path.IndexOf(':', StringComparison.Ordinal);
        return colon > 1 && ValidRemoteName(path[..colon]) ? path[..colon] : null;
    }

    /// <summary>Each remote's <c>type</c> in an rclone credential file. Only section names and types are read.</summary>
    public static IReadOnlyDictionary<string, string> RemoteTypes(string configPath)
    {
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(configPath)) return types;
        string? section = null;
        foreach (string raw in File.ReadLines(configPath))
        {
            string line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
            if (section is null || !line.StartsWith("type", StringComparison.Ordinal)) continue;
            string[] pair = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length == 2 && pair[0] == "type") types[section] = pair[1];
        }
        return types;
    }

    public SignInResult SignIn(string remoteName, CloudProvider? provider, string? configPath, CancellationToken cancel)
    {
        if (!ValidRemoteName(remoteName))
            return new(false, "Choose a remote name of 1-32 letters, digits, '-' or '_', such as gdrive.");
        string path = Environment.ExpandEnvironmentVariables(string.IsNullOrWhiteSpace(configPath) ? DefaultConfigPath(state) : configPath.Trim());
        string? existingType = RemoteTypes(path).GetValueOrDefault(remoteName);
        var chosen = provider ?? FromType(existingType);
        if (chosen is not { } kind)
            return new(false, existingType is null
                ? $"Remote '{remoteName}' is not in {path}. Choose Google Drive, OneDrive or Dropbox to create it."
                : $"Remote '{remoteName}' is a '{existingType}' remote, which Medal Sync cannot sign in to. Choose another name.");
        string type = RcloneType(kind);
        if (existingType is not null && existingType != type)
            return new(false, $"Remote '{remoteName}' already exists as a '{existingType}' remote. Choose another name for {kind}; nothing was changed.");

        Directory.CreateDirectory(state.Root);
        string work = Path.Combine(state.Root, "sign-in-" + Guid.NewGuid().ToString("N") + ".conf");
        try
        {
            if (File.Exists(path)) File.Copy(path, work);
            else File.WriteAllBytes(work, []);
            List<string> arguments = existingType is null
                ? ["config", "create", remoteName, type, .. (kind == CloudProvider.GoogleDrive ? new[] { "scope", "drive" } : []), "--config", work, "--auto-confirm"]
                : ["config", "reconnect", remoteName + ":", "--config", work, "--auto-confirm"];
            (run ?? Run)(arguments, cancel);
            cancel.ThrowIfCancellationRequested();
            if (RemoteTypes(work).GetValueOrDefault(remoteName) != type)
                return new(false, $"rclone did not finish setting up '{remoteName}'. The credential file is unchanged; try again.");
            var (result, detail) = (check ?? Check)(remoteName + ":", work, cancel);
            // A cancel during the check (up to its call timeout) must still keep the credential file as it was.
            cancel.ThrowIfCancellationRequested();
            if (result != StorageCheckResult.Ok)
                return new(false, $"The sign-in did not pass the storage check: {detail} The credential file is unchanged.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            AtomicFile.Replace(path, File.ReadAllBytes(work));
            return new(true, $"Signed in: '{remoteName}' ({kind}) passed the storage check and is saved in {path}.", path);
        }
        catch (OperationCanceledException)
        {
            return new(false, "Sign-in was cancelled. The credential file is unchanged.");
        }
        catch (RcloneFailedException ex)
        {
            return new(false, (ex.TimedOut ? "Sign-in was not finished in time." : "rclone could not finish signing in.")
                + " The credential file is unchanged; try again.");
        }
        finally
        {
            if (File.Exists(work)) File.Delete(work);
        }
    }

    private string Run(IReadOnlyList<string> arguments, CancellationToken cancel) =>
        new RcloneRunner(rclonePath).Run(arguments, SignInTimeout, cancel.ThrowIfCancellationRequested);

    private (StorageCheckResult, string) Check(string destination, string credentials, CancellationToken cancel) =>
        StorageCheck.CheckSignIn(destination, credentials, arguments => new RcloneRunner(rclonePath)
            .Run(arguments, StorageCheck.CallTimeout, cancel.ThrowIfCancellationRequested, captureOutput: true));
}
