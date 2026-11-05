using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Platform;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

public sealed class ReplicaCheckStatusTests : IDisposable
{
    private readonly MedalFixture _fixture = new();
    private readonly DateTimeOffset _now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private const string Library = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private LocalState State => new(_fixture.PathFor("state"));

    private ReplicaAuditAttempt Attempt(string result = ReplicaAudit.Failed, string? reason = null, int minutesAgo = 10) =>
        new("vault", _now.AddMinutes(-minutesAgo).AddSeconds(-1), _now.AddMinutes(-minutesAgo), 12, new string('b', 64), result, reason,
            4, result == ReplicaAudit.Incomplete ? 0 : 4, result == ReplicaAudit.Passed ? 4 : result == ReplicaAudit.Incomplete ? 0 : 2,
            result == ReplicaAudit.Failed ? 1 : 0, result == ReplicaAudit.Failed ? 1 : 0, result == ReplicaAudit.Incomplete ? 4 : 0, 1024);

    private void History(ReplicaAuditAttempt last, ReplicaAuditAttempt? complete = null, string library = Library)
    {
        Directory.CreateDirectory(Path.Combine(State.Root, "replication"));
        complete ??= last.Complete ? last : null;
        File.WriteAllBytes(ReplicaAudit.HistoryPath(State), Json.Serialize(new ReplicaAuditHistory(1, library,
            [new("vault", complete, complete?.Result == ReplicaAudit.Passed ? complete : null, complete is null ? [last] : [last, complete])])));
    }

    private CloudCopyCheckStatus Read() => Assert.Single(ReplicaCheckStatus.Read(State, Library, _now));

    private void Verified(string[] targets, DateTimeOffset? at = null, string library = Library, int format = 1) =>
        File.WriteAllBytes(Path.Combine(State.Root, "replication", "last-verified.json"), Json.Serialize(new
        {
            format, libraryId = library, verifiedAt = at ?? _now.AddMinutes(-1), targets,
        }));

    [Fact]
    public void IncompleteSignInDoesNotHideTheLastCompleteFailure()
    {
        History(Attempt(ReplicaAudit.Incomplete, "sign-in", 5), Attempt());
        var check = Read();
        Assert.Equal(ReplicaAudit.Incomplete, check.LastCheck!.Result);
        Assert.Equal(ReplicaAudit.Failed, check.LastComplete!.Result);
        Assert.True(check.NeedsAttention);
        Assert.Contains(check.Messages(), m => m.Contains("1 missing, 1 different", StringComparison.Ordinal));
        Assert.Contains(check.Messages(), m => m.Contains("Sign in again", StringComparison.Ordinal));
    }

    [Fact]
    public void PassingCheckClearsTheWarningAndKeepsItsCounts()
    {
        History(Attempt(ReplicaAudit.Passed));
        var check = Read();
        Assert.False(check.NeedsAttention);
        Assert.Empty(check.Messages());
        Assert.Equal(4, check.LastCheck!.Matched);
    }

    [Theory]
    [InlineData("matching", true)]
    [InlineData("different-target", false)]
    [InlineData("old", false)]
    [InlineData("foreign-library", false)]
    [InlineData("future", false)]
    [InlineData("unknown-format", false)]
    [InlineData("malformed", false)]
    public void OnlyANewerReceiptForThisLibraryAndTargetClearsTheWarning(string receipt, bool cleared)
    {
        History(Attempt());
        Verified(receipt == "different-target" ? ["other"] : ["VAULT"],
            receipt == "old" ? _now.AddHours(-1) : receipt == "future" ? _now.AddHours(1) : null,
            receipt == "foreign-library" ? new string('c', 32) : Library, receipt == "unknown-format" ? 2 : 1);
        if (receipt == "malformed") File.WriteAllText(Path.Combine(State.Root, "replication", "last-verified.json"), "{\"format\":\"1\"}");
        var check = Read();
        Assert.Equal(!cleared, check.NeedsAttention);
        Assert.Equal(!cleared, check.Messages().Any());
        Assert.Equal(ReplicaAudit.Failed, check.LastCheck!.Result); // Clearing a notice never rewrites history.
    }

    [Fact]
    public void AVerifiedFailureDoesNotHideANewerIncompleteCheckOrRepeatTheResolvedDamageNotice()
    {
        History(Attempt(ReplicaAudit.Incomplete, "sign-in", 5), Attempt());
        Verified(["vault"], _now.AddMinutes(-7));
        var check = Read();
        Assert.True(check.NeedsAttention);
        Assert.False(check.Failed);
        Assert.True(check.Incomplete);
        Assert.Contains("Sign in again", Assert.Single(check.Messages()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("broken")]
    [InlineData("invalid-counts")]
    [InlineData("private-reason")]
    public void ForeignOrInvalidHistoryDoesNotInventAResult(string kind)
    {
        var attempt = Attempt();
        if (kind == "invalid-counts") attempt = attempt with { Missing = -1 };
        if (kind == "private-reason") attempt = attempt with { Reason = @"private-user C:\Secrets\token" };
        History(attempt, library: kind == "foreign" ? new string('c', 32) : Library);
        if (kind == "broken") File.WriteAllText(ReplicaAudit.HistoryPath(State), "{ not json");
        Assert.Empty(ReplicaCheckStatus.Read(State, Library, _now).SelectMany(c => c.Messages()));
        Assert.All(ReplicaCheckStatus.Read(State, Library, _now), c => Assert.Null(c.LastCheck));
    }

    [Fact]
    public void AReadFailureDoesNotInterruptStatusOrCreateState()
    {
        Assert.Empty(ReplicaCheckStatus.Read(State, Library, _now));
        Assert.False(Directory.Exists(State.Root));
        History(Attempt());
        using var locked = new FileStream(ReplicaAudit.HistoryPath(State), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Empty(ReplicaCheckStatus.Read(State, Library, _now));
    }

    [Fact]
    public void ConfiguredTargetsIgnoreRetiredHistoryAndReportUncheckedCopiesAsUnknown()
    {
        History(Attempt());
        var check = Assert.Single(ReplicaCheckStatus.Read(State, Library, _now, [new("new-copy", "remote:folder")]));
        Assert.Equal("new-copy", check.Name);
        Assert.Null(check.LastCheck);
        Assert.False(check.NeedsAttention);
    }

    [Fact]
    public void CheckDamageTakesPriorityOverGenericPendingCopiesAndYieldsToAnExistingNotice()
    {
        History(Attempt());
        StorageReplication.MarkPending(State, Library, "vault", _now);
        var journal = Ownership.Initial(Library, "12345", _now);
        var status = DesktopStatus.Read(State, Library, journal, null, false, _now);
        Assert.Contains("failed its last complete check", status.Notice, StringComparison.Ordinal);
        File.WriteAllBytes(Path.Combine(State.Root, "desktop-notice.json"), Json.Serialize(new DesktopNotice("Higher-priority notice", _now)));
        Assert.Equal("Higher-priority notice", DesktopStatus.Read(State, Library, journal, null, false, _now).Notice);
    }

    public void Dispose() => _fixture.Dispose();
}
