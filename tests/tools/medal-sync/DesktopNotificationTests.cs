using MedalSync.Config;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Tests.Fixtures;

namespace MedalSync.Tests;

public sealed class DesktopNotificationTests : IDisposable
{
    private readonly MedalFixture _fixture = new();
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() => _fixture.Dispose();

    /// <summary>A status read. Active, Uploading and Verifying only ever describe this PC's own session.</summary>
    private static DesktopStatus S(SessionPhase phase, string next = "next action", string? attention = null, string? lastResult = null,
        bool? owned = null) =>
        new(phase, next, Now, LastResult: lastResult)
        {
            StorageAttention = attention,
            OwnedHere = owned ?? phase is SessionPhase.Active or SessionPhase.Uploading or SessionPhase.Verifying,
        };

    private static List<DesktopNotification> Run(params DesktopStatus[] reads)
    {
        var notifier = new DesktopNotifier();
        return reads.Select(notifier.Observe).Where(n => n is not null).Select(n => n!).ToList();
    }

    [Fact]
    public void AFinishThatReachesReadyNotifiesOnce()
    {
        var shown = Run(S(SessionPhase.Active), S(SessionPhase.Uploading), S(SessionPhase.Uploading), S(SessionPhase.Verifying),
            S(SessionPhase.Ready), S(SessionPhase.Ready), S(SessionPhase.Ready));
        var ready = Assert.Single(shown);
        Assert.Equal(DesktopNotificationKind.Ready, ready.Kind);
        Assert.Equal(DesktopNotifier.ReadyMessage, ready.Message);
    }

    [Fact]
    public void AQuickFinishBetweenReadsStillNotifiesReady()
    {
        var ready = Assert.Single(Run(S(SessionPhase.Active), S(SessionPhase.Ready)));
        Assert.Equal(DesktopNotificationKind.Ready, ready.Kind);
    }

    [Theory]
    [InlineData("Ready")]
    [InlineData("RecoveryNeeded")]
    [InlineData("Uploading")]
    public void TheFirstReadAfterTheViewStartsNeverNotifies(string phase) =>
        Assert.Empty(Run(S(Enum.Parse<SessionPhase>(phase), attention: "Storage needs attention: sign in again.")));

    [Fact]
    public void AViewStartedMidFinishStillNotifiesWhenItReachesReady()
    {
        var ready = Assert.Single(Run(S(SessionPhase.Uploading), S(SessionPhase.Verifying), S(SessionPhase.Ready)));
        Assert.Equal(DesktopNotificationKind.Ready, ready.Kind);
    }

    [Fact]
    public void AViewStartedDuringAnotherPcsRecoveryOrARefusedAccessDoesNotClaimThisPcsFinish()
    {
        Assert.Empty(Run(S(SessionPhase.RecoveryNeeded, owned: false), S(SessionPhase.Ready)));
        Assert.Empty(Run(S(SessionPhase.RecoveryNeeded, attention: "Sign in again.", owned: false), S(SessionPhase.Ready)));
        var ready = Assert.Single(Run(S(SessionPhase.RecoveryNeeded, owned: true), S(SessionPhase.Ready)));
        Assert.Equal(DesktopNotificationKind.Ready, ready.Kind);
    }

    [Fact]
    public void ARefusedCloudSignInAfterReadyNotifiesAsStorageAttention()
    {
        const string refused = "Google Drive refused Medal Sync's sign-in. Sign in again.";
        var shown = Run(S(SessionPhase.Ready), S(SessionPhase.RecoveryNeeded, refused, attention: refused, owned: false),
            S(SessionPhase.RecoveryNeeded, refused, attention: refused, owned: false));
        var attention = Assert.Single(shown);
        Assert.Equal((DesktopNotificationKind.StorageAttention, refused), (attention.Kind, attention.Message));
    }

    [Fact]
    public void ReadyAfterAnotherPcOrAGivenBackStartDoesNotNotify()
    {
        Assert.Empty(Run(S(SessionPhase.Ready), S(SessionPhase.Waiting), S(SessionPhase.Ready)));
        Assert.Empty(Run(S(SessionPhase.Ready), S(SessionPhase.Starting), S(SessionPhase.Ready)));
    }

    [Fact]
    public void WaitingForStorageNeverNotifiesButTheFinishThatFollowsDoes()
    {
        var shown = Run(S(SessionPhase.Active), S(SessionPhase.Uploading), S(SessionPhase.WaitingForStorage),
            S(SessionPhase.WaitingForStorage), S(SessionPhase.Uploading), S(SessionPhase.Ready));
        Assert.Equal([DesktopNotificationKind.Ready], shown.Select(n => n.Kind));
    }

    [Fact]
    public void WaitingForStorageWithoutASessionDoesNotClaimClipsWereUploaded() =>
        Assert.Empty(Run(S(SessionPhase.Ready), S(SessionPhase.WaitingForStorage), S(SessionPhase.Ready)));

    [Fact]
    public void ABlockedFinishNotifiesOnceWithThePanelsReasonAndTheRetryThatSucceedsNotifiesReady()
    {
        var shown = Run(S(SessionPhase.Active), S(SessionPhase.Verifying),
            S(SessionPhase.RecoveryNeeded, "Keep this PC's cache.", lastResult: "Removed clips need review."),
            S(SessionPhase.RecoveryNeeded, "Keep this PC's cache.", lastResult: "Removed clips need review."),
            S(SessionPhase.Uploading), S(SessionPhase.Ready));
        Assert.Equal([DesktopNotificationKind.NeedsAction, DesktopNotificationKind.Ready], shown.Select(n => n.Kind));
        Assert.Equal("Keep this PC's cache. Removed clips need review.", shown[0].Message);
    }

    [Fact]
    public void AnotherPcsInterruptedSessionDoesNotAskThisPcForAction() =>
        Assert.Empty(Run(S(SessionPhase.Ready), S(SessionPhase.Waiting), S(SessionPhase.RecoveryNeeded)));

    [Fact]
    public void UnreadableOrStaleReadsChangeNothing()
    {
        var shown = Run(S(SessionPhase.Active), S(SessionPhase.Verifying), S(SessionPhase.RecoveryNeeded), DesktopStatus.Unknown(),
            S(SessionPhase.RecoveryNeeded), DesktopStatus.Unknown(), S(SessionPhase.Ready), DesktopStatus.Unknown(), S(SessionPhase.Ready));
        Assert.Equal([DesktopNotificationKind.NeedsAction, DesktopNotificationKind.Ready], shown.Select(n => n.Kind));
        Assert.Empty(Run(DesktopStatus.Unknown(), S(SessionPhase.Ready)));
    }

    [Fact]
    public void StorageAttentionNotifiesOncePerChange()
    {
        const string signIn = "Storage needs attention: sign in again.";
        const string copy = "Cloud copy 'backup' failed its last complete check.";
        var shown = Run(S(SessionPhase.Ready), S(SessionPhase.Ready, attention: signIn), S(SessionPhase.Ready, attention: signIn),
            S(SessionPhase.Ready, attention: copy), S(SessionPhase.Ready), S(SessionPhase.Ready, attention: copy));
        Assert.Equal([signIn, copy, copy], shown.Select(n => n.Message));
        Assert.All(shown, n => Assert.Equal(DesktopNotificationKind.StorageAttention, n.Kind));
    }

    [Fact]
    public void PreferencesDefaultToOnAndSurviveARestart()
    {
        var state = new LocalState(_fixture.PathFor("state"));
        Assert.True(DesktopPreferences.Read(state).Notifications);
        (DesktopPreferences.Default with { Notifications = false }).Save(state);
        Assert.False(DesktopPreferences.Read(state).Notifications);
        File.WriteAllText(Path.Combine(state.Root, DesktopPreferences.FileName), "{ broken");
        Assert.True(DesktopPreferences.Read(state).Notifications);
        File.WriteAllText(Path.Combine(state.Root, DesktopPreferences.FileName), "{ \"format\": 2, \"notifications\": false }");
        Assert.True(DesktopPreferences.Read(state).Notifications);
    }
}
