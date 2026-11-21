using MedalSync.Config;
using MedalSync.Platform;
using MedalSync.Tests.Fixtures;

namespace MedalSync.Tests;

/// <summary>Signing in to a provider through a fake rclone: create, reconnect, cancel and failure (#253).</summary>
public sealed class ProviderSignInTests : IDisposable
{
    private const string Token = "synthetic-secret-token-never-shown";
    private readonly MedalFixture _fixture = new();
    private readonly LocalState _state;
    private readonly List<IReadOnlyList<string>> _calls = [];
    private Action<IReadOnlyList<string>, string, CancellationToken>? _rclone;
    private (StorageCheckResult, string) _check = (StorageCheckResult.Ok, "Remote gdrive: accepted rclone's sign-in.");
    private readonly List<string> _checkedWith = [];
    private Action? _duringCheck;

    public ProviderSignInTests() => _state = new LocalState(_fixture.PathFor("state"));

    public void Dispose() => _fixture.Dispose();

    /// <summary>The fake rclone writes the remote into the private copy it was given, as the real one does after sign-in.</summary>
    private ProviderSignIn SignIn() => new(@"C:\Tools\rclone.exe", _state, (arguments, cancel) =>
    {
        _calls.Add(arguments);
        string work = arguments[arguments.ToList().IndexOf("--config") + 1];
        Assert.StartsWith(_state.Root, work, StringComparison.OrdinalIgnoreCase);
        if (_rclone is not null) { _rclone(arguments, work, cancel); return ""; }
        if (arguments[1] == "create")
            File.AppendAllText(work, $"\n[{arguments[2]}]\ntype = {arguments[3]}\ntoken = {{\"access_token\":\"{Token}\"}}\n");
        else File.WriteAllText(work, File.ReadAllText(work).Replace("expired", Token, StringComparison.Ordinal));
        return "";
    }, (remote, credentials, _) => { _checkedWith.Add(remote + "|" + credentials); _duringCheck?.Invoke(); return _check; });

    private string Existing(string text)
    {
        string path = _fixture.PathFor("private", "rclone.conf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void CreatingARemoteWritesTheCredentialFileOnlyAfterTheCheckPasses()
    {
        var result = SignIn().SignIn("gdrive", CloudProvider.GoogleDrive, null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        string saved = ProviderSignIn.DefaultConfigPath(_state);
        Assert.Equal(saved, result.ConfigPath);
        Assert.Equal("drive", ProviderSignIn.RemoteTypes(saved)["gdrive"]);
        Assert.Equal(["config", "create", "gdrive", "drive", "scope", "drive"], _calls.Single().Take(6));
        Assert.Contains("--auto-confirm", _calls.Single());
        Assert.Single(_checkedWith, c => c.StartsWith("gdrive:|", StringComparison.Ordinal));
        Assert.DoesNotContain(Token, result.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_state.Root, "sign-in-*"));
    }

    [Fact]
    public void ReconnectingKeepsOtherRemotesAndUsesTheExistingType()
    {
        string path = Existing("[other]\ntype = alias\nremote = D:\\x\n\n[onedrive]\ntype = onedrive\ntoken = expired\n");

        var result = SignIn().SignIn("onedrive", null, path, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(["config", "reconnect", "onedrive:"], _calls.Single().Take(3));
        string text = File.ReadAllText(path);
        Assert.Contains("[other]", text, StringComparison.Ordinal);
        Assert.Contains(Token, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("failure")]
    [InlineData("check")]
    [InlineData("cancel-during-check")]
    [InlineData("incomplete")]
    public void ACancelledFailedOrUncheckedSignInLeavesTheCredentialFileUnchanged(string outcome)
    {
        string path = Existing("[gdrive]\ntype = drive\ntoken = expired\n");
        byte[] before = File.ReadAllBytes(path);
        using var cancel = new CancellationTokenSource();
        _rclone = outcome switch
        {
            "cancel" => (_, work, _) => { File.WriteAllText(work, "[gdrive]\ntype = drive\ntoken = half\n"); cancel.Cancel(); throw new OperationCanceledException(); },
            "failure" => (_, work, _) => { File.WriteAllText(work, "garbage"); throw new RcloneFailedException("synthetic failure", 1); },
            "incomplete" => (_, work, _) => File.WriteAllText(work, "[something-else]\ntype = drive\n"),
            _ => null,
        };
        if (outcome == "check") _check = (StorageCheckResult.SignInRequired, "The provider refused rclone's sign-in.");
        if (outcome == "cancel-during-check") _duringCheck = cancel.Cancel; // The check itself still reports Ok.

        var result = SignIn().SignIn("gdrive", null, path, cancel.Token);

        Assert.False(result.Succeeded);
        Assert.Null(result.ConfigPath);
        Assert.Contains("unchanged", result.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(_state.Root, "sign-in-*"));
    }

    [Fact]
    public void ARemoteOfAnotherTypeOrAnUnsafeNameIsRefusedBeforeRcloneRuns()
    {
        string path = Existing("[gdrive]\ntype = crypt\n");
        Assert.False(SignIn().SignIn("gdrive", CloudProvider.GoogleDrive, path, CancellationToken.None).Succeeded);
        Assert.False(SignIn().SignIn("gdrive", null, path, CancellationToken.None).Succeeded);
        Assert.False(SignIn().SignIn("missing", null, path, CancellationToken.None).Succeeded);
        Assert.False(SignIn().SignIn("bad name;", CloudProvider.Dropbox, path, CancellationToken.None).Succeeded);
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData("gdrive:MedalSync", "gdrive")]
    [InlineData("onedrive:", "onedrive")]
    [InlineData(@"\\server\share", null)]
    [InlineData(@"C:\Folder", null)]
    public void TheRemoteNameIsReadFromAnRclonePath(string path, string? remote) => Assert.Equal(remote, ProviderSignIn.RemoteOf(path));

    [Fact]
    public void TheStatusPanelOffersSignInAgainOnlyForAnExpiredSignIn()
    {
        string library = new('c', 32);
        var journal = MedalSync.Coordination.Ownership.Initial(library, "12345", DateTimeOffset.UtcNow);
        Assert.Null(DesktopStatus.Read(_state, library, journal, null, false, DateTimeOffset.UtcNow).SignInRemote);
        Directory.CreateDirectory(_state.Root);
        new StorageCheckReport(1, library, DateTimeOffset.UtcNow,
        [
            new("primary", "gdrive:MedalSync/Clips", true, true, StorageCheckResult.SignInRequired, "Sign in again."),
            new("vault", "onedrive:Vault", false, false, StorageCheckResult.Offline, "Offline."),
        ]).Save(_state);
        Assert.Equal("gdrive", DesktopStatus.Read(_state, library, journal, null, false, DateTimeOffset.UtcNow).SignInRemote);
        new StorageCheckReport(1, library, DateTimeOffset.UtcNow,
            [new("primary", "gdrive:MedalSync/Clips", true, true, StorageCheckResult.FolderMissing, "Missing.")]).Save(_state);
        Assert.Null(DesktopStatus.Read(_state, library, journal, null, false, DateTimeOffset.UtcNow).SignInRemote);
    }

    [Fact]
    public void TheSignInCheckClassifiesAnExpiredSignIn()
    {
        var (result, _) = StorageCheck.CheckSignIn("gdrive:", @"C:\private\rclone.conf",
            _ => throw new RcloneFailedException("synthetic", 1, hints: RcloneHints.SignIn));
        Assert.Equal(StorageCheckResult.SignInRequired, result);
        Assert.Equal(StorageCheckResult.Ok, StorageCheck.CheckSignIn("gdrive:", @"C:\private\rclone.conf", _ => "").Result);
    }
}
