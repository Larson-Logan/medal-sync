using System.Text.Json.Nodes;
using MedalSync.Config;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

/// <summary>Read-only sign-in checks of rclone remotes, with a stub rclone for each outcome.</summary>
public sealed class StorageCheckTests : IDisposable
{
    private static readonly string LibraryId = new('a', 32);
    private const string Credentials = @"C:\Private\rclone.conf";
    private readonly MedalFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static MedalSyncConfig Config(CredentialCheckPolicy policy = CredentialCheckPolicy.Warn) => new(1, LibraryId, MedalFixture.AccountId,
        @"\\server\share\Journal", "onedrive:MedalSync", @"M:\Clips", @"E:\Cache", @"C:\Tools\rclone.exe",
        new(5, 20, 20, 300, 30, 1000, 2, 53682), 4, [])
    {
        Replication = new(@"E:\MedalLibrary", Credentials,
            [new ReplicaTarget("backup", "dropbox:MedalSync"), new ReplicaTarget("vault", "gdrive:Medal/") { Required = true }])
        { CredentialCheck = policy },
    };

    private static readonly DateTimeOffset Now = new(2026, 10, 29, 20, 0, 0, TimeSpan.Zero);

    private static StorageCheckReport Check(Func<IReadOnlyList<string>, string> run, MedalSyncConfig? config = null) =>
        StorageCheck.Run(config ?? Config(), Now, run, path => path == Credentials);

    private static string Healthy(IReadOnlyList<string> args) => args[0] == "cat" ? LibraryId + "\n" : LibraryId + "/\nother/\n";

    private static StorageCheckItem Item(StorageCheckReport report, string name) => Assert.Single(report.Remotes, r => r.Name == name);

    [Fact]
    public void HealthyRemotesAreOkAndOnlyReadCommandsRun()
    {
        var calls = new System.Collections.Concurrent.ConcurrentBag<IReadOnlyList<string>>();
        var report = Check(args => { calls.Add(args); return Healthy(args); });

        Assert.All(report.Remotes, r => Assert.Equal(StorageCheckResult.Ok, r.Result));
        Assert.Equal(["primary", "backup", "vault"], report.Remotes.Select(r => r.Name));
        Assert.Equal(0, report.ExitCode);
        Assert.Equal(3, calls.Count);
        Assert.All(calls, args =>
        {
            Assert.Contains(args[0], new[] { "cat", "lsf" });
            // Prompt disabling is enforced by RcloneRunner at the process boundary.
            Assert.DoesNotContain("--ask-password=false", args);
            Assert.Equal(Credentials, args[args.ToList().IndexOf("--config") + 1]);
        });
        Assert.Contains(calls, args => args[0] == "cat" && args[1] == "onedrive:MedalSync/Clips/.medal-sync-library");
        Assert.Contains(calls, args => args[0] == "lsf" && args[1] == "gdrive:Medal");
        Assert.Equal("gdrive:Medal", Item(report, "vault").Destination);
    }

    [Fact]
    public void ATargetWithoutThisLibrarysFolderIsOkAndSaysSo()
    {
        var report = Check(args => args[0] == "cat" ? LibraryId : "other/\n");

        var backup = Item(report, "backup");
        Assert.Equal(StorageCheckResult.Ok, backup.Result);
        Assert.Contains("no copy of this library yet", backup.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingCredentialFileIsReportedWithoutRunningRclone()
    {
        var report = StorageCheck.Run(Config(), Now, _ => throw new InvalidOperationException("rclone must not run."), _ => false);

        Assert.All(report.Remotes, r => Assert.Equal(StorageCheckResult.ConfigurationProblem, r.Result));
        Assert.Contains("credential file is missing", report.Remotes[0].Detail, StringComparison.Ordinal);
        Assert.Equal(1, report.ExitCode);
    }

    [Fact]
    public void AnExpiredSignInNamesTheReconnectCommand()
    {
        var report = Check(args => args[1].StartsWith("dropbox:", StringComparison.Ordinal)
            ? throw new RcloneFailedException("synthetic", 1, hints: RcloneHints.SignIn, command: "lsf") : Healthy(args));

        var backup = Item(report, "backup");
        Assert.Equal(StorageCheckResult.SignInRequired, backup.Result);
        Assert.Contains("rclone config reconnect \"dropbox:\" --config \"" + Credentials + "\"", backup.Detail, StringComparison.Ordinal);
        Assert.True(backup.NeedsAction);
        Assert.False(backup.Essential);
        Assert.Equal(1, report.ExitCode);
    }

    [Fact]
    public void AnUnreadableConfigurationIsAConfigurationProblem()
    {
        var report = Check(args => args[0] == "cat"
            ? throw new RcloneFailedException("synthetic", 1, hints: RcloneHints.Config, command: "cat") : Healthy(args));

        Assert.Equal(StorageCheckResult.ConfigurationProblem, Item(report, "primary").Result);
        Assert.Contains("opens without a password prompt", Item(report, "primary").Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, (int)RcloneHints.Network, false)]
    [InlineData(5, 0, false)]
    [InlineData(null, 0, true)]
    public void AnOutageIsOfflineAndNeedsNothingFromTheUser(int? exitCode, int hints, bool timedOut)
    {
        var report = Check(_ => throw new RcloneFailedException("synthetic", exitCode, hints: (RcloneHints)hints, timedOut: timedOut, command: "lsf"));

        Assert.All(report.Remotes, r => Assert.Equal(StorageCheckResult.Offline, r.Result));
        Assert.All(report.Remotes, r => Assert.False(r.NeedsAction));
        Assert.Equal(3, report.ExitCode);
    }

    [Fact]
    public void APrimaryFolderThatDoesNotExistIsFolderMissing()
    {
        var report = Check(args => args[0] == "cat" ? throw new RcloneFailedException("synthetic", 3, command: "cat")
            : args[1].StartsWith("onedrive:", StringComparison.Ordinal) ? throw new RcloneFailedException("synthetic", 3, command: "lsf") : Healthy(args));

        var primary = Item(report, "primary");
        Assert.Equal(StorageCheckResult.FolderMissing, primary.Result);
        Assert.True(primary.NeedsAction);
        Assert.Contains("storage init-primary", primary.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void APrimaryFolderWithoutItsMarkerIsMarkerMissing()
    {
        var report = Check(args => args[0] == "cat" ? throw new RcloneFailedException("synthetic", 4, command: "cat") : Healthy(args));

        Assert.Equal(StorageCheckResult.MarkerMissing, Item(report, "primary").Result);
    }

    [Fact]
    public void APrimaryMarkerForAnotherLibraryIsRefused()
    {
        var report = Check(args => args[0] == "cat" ? new string('b', 32) : Healthy(args));

        Assert.Equal(StorageCheckResult.WrongLibrary, Item(report, "primary").Result);
        Assert.DoesNotContain(new string('b', 32), Item(report, "primary").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingTargetFolderIsReportedButTheFirstCopyCreatesIt()
    {
        var report = Check(args => args[1].StartsWith("gdrive:", StringComparison.Ordinal)
            ? throw new RcloneFailedException("synthetic", 3, command: "lsf") : Healthy(args));

        var vault = Item(report, "vault");
        Assert.Equal(StorageCheckResult.FolderMissing, vault.Result);
        Assert.False(vault.NeedsAction);
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void RcloneThatCannotStartIsAFailureNotAnOutage()
    {
        var report = Check(_ => throw new RcloneFailedException("Cannot start rclone. Check rclonePath and retry."));

        Assert.All(report.Remotes, r => Assert.Equal(StorageCheckResult.Failed, r.Result));
        Assert.Contains("Check rclonePath", report.Remotes[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AShutdownDuringTheCheckCountsAsUnchecked()
    {
        var report = Check(_ => throw new OperationCanceledException("Windows shutdown budget expired."));

        Assert.All(report.Remotes, r => Assert.Equal(StorageCheckResult.Offline, r.Result));
    }

    [Fact]
    public void AConfigurationWithoutRemotesHasNothingToCheck()
    {
        var config = Config() with { Remote = @"\\server\share\Media", Replication = null };
        var report = StorageCheck.Run(config, Now, _ => throw new InvalidOperationException("rclone must not run."), _ => true);

        Assert.False(StorageCheck.HasRemotes(config));
        Assert.Empty(report.Remotes);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal("No rclone remotes are configured; there is nothing to check.", report.ToText());
    }

    private static StorageCheckReport Failing(params string[] names) => Check(args =>
        names.Any(n => n == "primary" ? args[0] == "cat" : args[1].StartsWith(n == "backup" ? "dropbox:" : "gdrive:", StringComparison.Ordinal))
            ? throw new RcloneFailedException("synthetic", 1, hints: RcloneHints.SignIn, command: args[0]) : Healthy(args));

    [Fact]
    public void WarnIsTheDefaultAndStartsMedalWithANotice()
    {
        var verdict = StorageCheck.Verdict(Failing("primary", "backup"), Config().Replication!.CredentialCheck);

        Assert.Null(verdict.Refusal);
        Assert.StartsWith("Storage check: Primary storage onedrive:MedalSync/Clips did not accept", verdict.Warning, StringComparison.Ordinal);
        Assert.Contains("dropbox:", verdict.Warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("primary")]
    [InlineData("vault")]
    public void RefuseKeepsMedalClosedForThePrimaryOrARequiredTarget(string failing)
    {
        var verdict = StorageCheck.Verdict(Failing(failing), CredentialCheckPolicy.Refuse);

        Assert.Null(verdict.Warning);
        Assert.Contains("credentialCheck is \"REFUSE\"", verdict.Refusal, StringComparison.Ordinal);
        Assert.Contains("rclone config reconnect", verdict.Refusal, StringComparison.Ordinal);
        Assert.EndsWith("the library stays Ready.", verdict.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void RefuseOnlyWarnsForAnOptionalTarget()
    {
        var verdict = StorageCheck.Verdict(Failing("backup"), CredentialCheckPolicy.Refuse);

        Assert.Null(verdict.Refusal);
        Assert.NotNull(verdict.Warning);
    }

    [Fact]
    public void AnOutageNeverRefusesOrWarns()
    {
        var report = Check(_ => throw new RcloneFailedException("synthetic", 1, hints: RcloneHints.Network, command: "lsf"));

        Assert.Equal(LaunchPreflight.Proceed, StorageCheck.Verdict(report, CredentialCheckPolicy.Refuse));
    }

    [Fact]
    public void TheLatestReportIsKeptForThisLibraryOnly()
    {
        var state = new LocalState(_fixture.PathFor("state"));
        Assert.Null(StorageCheckReport.ReadLast(state, LibraryId));
        var report = Failing("backup");
        report.Save(state);

        var read = StorageCheckReport.ReadLast(state, LibraryId);
        Assert.NotNull(read);
        Assert.Equal(report.Remotes, read.Remotes);
        Assert.Null(StorageCheckReport.ReadLast(state, new string('c', 32)));
        Assert.Equal(["backup"], StorageCheckReport.Outstanding(state, LibraryId).Select(p => p.Name));

        File.WriteAllText(StorageCheckReport.PathFor(state), "{ not json");
        Assert.Null(StorageCheckReport.ReadLast(state, LibraryId));
        Assert.Empty(StorageCheckReport.Outstanding(state, LibraryId));
    }

    [Fact]
    public void AHandoffVerifiedAfterTheCheckClearsAllButStillPendingCopies()
    {
        var state = new LocalState(_fixture.PathFor("state"));
        Failing("primary", "backup", "vault").Save(state);
        Assert.Equal(3, StorageCheckReport.Outstanding(state, LibraryId).Count);

        File.WriteAllText(Path.Combine(state.Root, "replication", "last-verified.json"),
            $$"""{ "format": 1, "libraryId": "{{LibraryId}}", "verifiedAt": "{{Now.AddMinutes(30):O}}" }""");
        Assert.Empty(StorageCheckReport.Outstanding(state, LibraryId));

        File.WriteAllText(Path.Combine(state.Root, "replication", "pending.json"),
            $$"""{ "format": 1, "libraryId": "{{LibraryId}}", "targets": [ { "name": "backup", "since": "{{Now:O}}", "lastAttempt": "{{Now.AddMinutes(30):O}}" } ] }""");
        Assert.Equal(["backup"], StorageCheckReport.Outstanding(state, LibraryId).Select(p => p.Name));
    }

    [Fact]
    public void TextNamesEachResultAndMarksOptionalCopies()
    {
        string text = Failing("backup").ToText();

        Assert.StartsWith("Checked 3 rclone remote(s) at 2026-10-29 20:00 UTC.", text, StringComparison.Ordinal);
        Assert.Contains("  Sign in again: Cloud copy at dropbox:MedalSync did not accept", text, StringComparison.Ordinal);
        Assert.Contains("(optional copy)", text, StringComparison.Ordinal);
        Assert.Contains("  OK: Primary storage onedrive:MedalSync/Clips accepted rclone's sign-in and identifies this library.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialCheckDefaultsToWarnAndIsOnlyWrittenWhenChanged()
    {
        var document = JsonNode.Parse(Json.Serialize(Config()))!.AsObject();
        Assert.False(document["replication"]!.AsObject().ContainsKey("credentialCheck"));

        document["replication"]!["credentialCheck"] = "REFUSE";
        string path = _fixture.PathFor("config.json");
        File.WriteAllText(path, document.ToJsonString());
        Assert.Equal(CredentialCheckPolicy.Refuse, MedalSyncConfig.Load(path).Replication!.CredentialCheck);

        document["replication"]!["credentialCheck"] = "ASK";
        File.WriteAllText(path, document.ToJsonString());
        Assert.Throws<InvalidDataException>(() => MedalSyncConfig.Load(path));
    }
}
