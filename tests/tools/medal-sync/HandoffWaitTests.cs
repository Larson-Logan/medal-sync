using MedalSync.Cli;
using MedalSync.Coordination;
using MedalSync.Session;
using MedalSync.Util;

namespace MedalSync.Tests;

/// <summary>An explicitly chosen wait for another PC to hand off the library (synthetic stores and clock).</summary>
public sealed partial class SessionTests
{
    private static readonly DeviceIdentity ThirdPc = new("cccccccccccccccccccccccccccccccc", "Third PC");

    private static HandoffWait WaitFor(TimeSpan timeout, List<HandoffWaitUpdate> updates, Func<bool>? cancelled = null) =>
        new(timeout, cancelled, updates.Add);

    private (Pc Pc, string Session) FirstPcHoldsTheLibrary()
    {
        var first = Computer(FirstPc, "first");
        AssertStarted(first.Coordinator.Launch());
        return (first, _store.ReadJournal().SessionId!);
    }

    private static void Release(Pc pc, string session)
    {
        pc.Medal.Running = false;
        var finished = pc.Coordinator.Finish(session);
        Assert.True(finished.Status is FinishStatus.Published or FinishStatus.Unchanged, $"{finished.Status}: {finished.Message}");
    }

    [Fact]
    public void WaitingStartsMedalOnceTheOwnerReleasesAndKeepsOneRequestThroughout()
    {
        var (first, session) = FirstPcHoldsTheLibrary();
        var second = Computer(SecondPc, "second");
        var updates = new List<HandoffWaitUpdate>();
        var seenRequests = new HashSet<string>();
        bool released = false;
        _clock.OnSleep = () =>
        {
            foreach (var request in _store.PendingRequests(_clock.Now)) seenRequests.Add(request.RequestId);
            if (!released && updates.Count >= 3)
            {
                released = true;
                Release(first, session);
            }
        };

        var outcome = second.Coordinator.LaunchWhenReleased(WaitFor(TimeSpan.FromMinutes(5), updates));
        _clock.OnSleep = null;

        AssertStarted(outcome);
        Assert.Equal(1, second.Medal.Launches);
        Assert.Equal(SecondPc, _store.ReadJournal().Owner);
        Assert.Single(seenRequests);
        Assert.Empty(_store.PendingRequests(_clock.Now));
        Assert.All(updates, u => Assert.True(u.Fresh && u.Owner == FirstPc && u.State == OwnershipState.Active));
        Assert.Contains("First PC has the library (in use)", updates[0].Describe(), StringComparison.Ordinal);
        Assert.Equal("Waiting for First PC to hand off the library", updates[0].Lines().Title);
    }

    [Fact]
    public void TimeoutBacksOffWithdrawsTheRequestAndLeavesTheOwnerAlone()
    {
        var (first, _) = FirstPcHoldsTheLibrary();
        var before = _store.ReadJournal();
        var second = Computer(SecondPc, "second");
        var updates = new List<HandoffWaitUpdate>();
        var checks = new List<DateTimeOffset>();
        var wait = new HandoffWait(TimeSpan.FromMinutes(5), Update: u => { updates.Add(u); checks.Add(_clock.Now); });

        var outcome = second.Coordinator.LaunchWhenReleased(wait);

        Assert.Equal(LaunchStatus.Waiting, outcome.Status);
        Assert.Contains("Stopped waiting after 5 minutes: First PC still has the library (in use)", outcome.Message, StringComparison.Ordinal);
        Assert.False(outcome.StorageOffline);
        Assert.Equal(before, _store.ReadJournal());
        Assert.Empty(_store.PendingRequests(_clock.Now));
        Assert.Empty(second.Mount.Starts);
        Assert.Equal(0, second.Medal.Launches);
        Assert.Equal(1, first.Medal.Launches);
        var gaps = checks.Zip(checks.Skip(1), (a, b) => b - a).ToList();
        Assert.Equal(wait.FirstPoll, gaps[0]);
        Assert.Equal(wait.FirstPoll * 2, gaps[1]);
        Assert.All(gaps, g => Assert.InRange(g, wait.FirstPoll, wait.MaxPoll));
        Assert.Equal(wait.MaxPoll, gaps[^1]);
        // Backoff keeps the number of journal reads small over the whole wait.
        Assert.InRange(updates.Count, 5, 15);
    }

    [Fact]
    public void StoppingTheWaitKeepsANewerRequestFromThisPc()
    {
        FirstPcHoldsTheLibrary();
        var second = Computer(SecondPc, "second");
        var updates = new List<HandoffWaitUpdate>();
        var newer = new HandoffRequest(1, SecondPc, SessionCoordinator.NewId(), _clock.Now);
        _clock.OnSleep = () => { if (updates.Count == 1) _store.WriteRequest(newer); };

        var outcome = second.Coordinator.LaunchWhenReleased(WaitFor(TimeSpan.FromMinutes(1), updates));
        _clock.OnSleep = null;

        Assert.Equal(LaunchStatus.Waiting, outcome.Status);
        Assert.Equal(newer, Assert.Single(_store.PendingRequests(_clock.Now)));
    }

    [Fact]
    public void CancellingTheWaitChangesNothingAndNeverOpensMedal()
    {
        FirstPcHoldsTheLibrary();
        var before = _store.ReadJournal();
        var second = Computer(SecondPc, "second");
        var updates = new List<HandoffWaitUpdate>();

        var outcome = second.Coordinator.LaunchWhenReleased(WaitFor(TimeSpan.FromMinutes(10), updates, () => updates.Count >= 2));

        Assert.Equal(LaunchStatus.Waiting, outcome.Status);
        Assert.StartsWith("Stopped waiting for the other PC", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(2, updates.Count);
        Assert.Equal(before, _store.ReadJournal());
        Assert.Empty(_store.PendingRequests(_clock.Now));
        Assert.Empty(second.Mount.Starts);
        Assert.Equal(0, second.Medal.Launches);
    }

    [Fact]
    public void CancellingWhileStorageIsPreparedGivesTheStartBackWithoutOpeningMedal()
    {
        var (first, session) = FirstPcHoldsTheLibrary();
        bool cancelled = false;
        var second = Computer(SecondPc, "second", prepareNativeBackup: () => cancelled = true);
        var updates = new List<HandoffWaitUpdate>();
        bool released = false;
        _clock.OnSleep = () =>
        {
            if (released || updates.Count == 0) return;
            released = true;
            Release(first, session);
        };

        var outcome = second.Coordinator.LaunchWhenReleased(WaitFor(TimeSpan.FromMinutes(5), updates, () => cancelled));
        _clock.OnSleep = null;

        Assert.Equal(LaunchStatus.Waiting, outcome.Status);
        Assert.StartsWith("Stopped waiting for the other PC", outcome.Message, StringComparison.Ordinal);
        Assert.Single(second.Mount.Starts);
        Assert.False(second.Mount.IsRunning);
        Assert.Equal(0, second.Medal.Launches);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Empty(_store.PendingRequests(_clock.Now));
    }

    [Fact]
    public void AReleaseExactlyAtTheDeadlineDoesNotStartMedal()
    {
        var (first, session) = FirstPcHoldsTheLibrary();
        var second = Computer(SecondPc, "second");
        var updates = new List<HandoffWaitUpdate>();
        var deadline = _clock.Now + TimeSpan.FromMinutes(1);
        bool released = false;
        _clock.OnSleep = () =>
        {
            if (released || _clock.Now < deadline) return;
            released = true;
            Release(first, session);
        };

        var outcome = second.Coordinator.LaunchWhenReleased(WaitFor(TimeSpan.FromMinutes(1), updates));
        _clock.OnSleep = null;

        Assert.True(released);
        Assert.Equal(LaunchStatus.Waiting, outcome.Status);
        Assert.StartsWith("Stopped waiting after 60 seconds", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(second.Mount.Starts);
        Assert.Equal(0, second.Medal.Launches);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Empty(_store.PendingRequests(_clock.Now));
    }

    [Fact]
    public void ACheckThatOutlastsTheDeadlineNeverAcquires()
    {
        var second = Computer(SecondPc, "second", preflight: () =>
        {
            // A slow provider check after a Clean read.
            _clock.Sleep(TimeSpan.FromSeconds(45));
            return LaunchPreflight.Proceed;
        });

        var outcome = second.Coordinator.LaunchWhenReleased(new HandoffWait(TimeSpan.FromSeconds(30)));

        Assert.Equal(LaunchStatus.Waiting, outcome.Status);
        Assert.Contains("handed off the library just as the wait ended", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(second.Mount.Starts);
        Assert.Equal(0, second.Medal.Launches);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
    }

    [Fact]
    public void CancellingDuringTheFinalOwnershipUpdatesReleasesInsteadOfOpeningMedal()
    {
        int checks = 0, atBackup = 0;
        var second = Computer(SecondPc, "second", prepareNativeBackup: () => atBackup = checks);
        // Not yet cancelled at the check right after the native backup; cancelled by the final launch decision.
        bool Cancelled() => ++checks > 0 && atBackup > 0 && checks > atBackup + 1;

        var outcome = second.Coordinator.LaunchWhenReleased(new HandoffWait(TimeSpan.FromMinutes(5), Cancelled));

        Assert.True(atBackup > 0);
        Assert.Equal(LaunchStatus.Waiting, outcome.Status);
        Assert.StartsWith("Stopped waiting for the other PC", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(0, second.Medal.Launches);
        Assert.False(second.Mount.IsRunning);
        Assert.Equal(OwnershipState.Clean, _store.ReadJournal().State);
        Assert.Empty(_store.PendingRequests(_clock.Now));
    }

    [Fact]
    public void WithTwoWaitingPcsOnlyOneAcquiresAndTheOtherKeepsWaiting()
    {
        var (first, session) = FirstPcHoldsTheLibrary();
        var second = Computer(SecondPc, "second");
        var third = Computer(ThirdPc, "third");
        var updates = new List<HandoffWaitUpdate>();
        bool released = false;
        _clock.OnSleep = () =>
        {
            if (released || updates.Count == 0) return;
            released = true;
            Release(first, session);
            // The third PC's own wait sees the release first.
            AssertStarted(third.Coordinator.LaunchWhenReleased(new HandoffWait(TimeSpan.FromMinutes(1))));
        };

        var outcome = second.Coordinator.LaunchWhenReleased(WaitFor(TimeSpan.FromMinutes(2), updates));
        _clock.OnSleep = null;

        Assert.Equal(LaunchStatus.Waiting, outcome.Status);
        Assert.Contains("Third PC still has the library (in use)", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(ThirdPc, _store.ReadJournal().Owner);
        Assert.Equal(1, third.Medal.Launches);
        Assert.Equal(0, second.Medal.Launches);
        Assert.Contains(updates, u => u.Owner == ThirdPc);
        Assert.Empty(_store.PendingRequests(_clock.Now));
    }

    [Fact]
    public void LosingTheAcquireRaceAfterSeeingCleanKeepsWaiting()
    {
        var third = Computer(ThirdPc, "third");
        bool raced = false;
        var second = Computer(SecondPc, "second", preflight: () =>
        {
            // Between this PC's Clean read and its acquisition, another PC takes the library.
            if (!raced) { raced = true; AssertStarted(third.Coordinator.Launch()); }
            return LaunchPreflight.Proceed;
        });
        var updates = new List<HandoffWaitUpdate>();

        var outcome = second.Coordinator.LaunchWhenReleased(WaitFor(TimeSpan.FromMinutes(1), updates));

        Assert.True(raced);
        Assert.Equal(LaunchStatus.Waiting, outcome.Status);
        Assert.StartsWith("Stopped waiting after 60 seconds", outcome.Message, StringComparison.Ordinal);
        Assert.Equal("Another PC took the library first. Still waiting for it to hand off.", updates[0].Describe());
        Assert.Contains(updates, u => u.Owner == ThirdPc);
        Assert.Equal(ThirdPc, _store.ReadJournal().Owner);
        Assert.Empty(second.Mount.Starts);
    }

    [Fact]
    public void AnUnreachableServerIsNeverTakenAsARelease()
    {
        var (first, session) = FirstPcHoldsTheLibrary();
        var second = Computer(SecondPc, "second");
        var updates = new List<HandoffWaitUpdate>();
        string offline = _store.Root + "-offline";
        bool moved = false, restored = false, released = false;
        _clock.OnSleep = () =>
        {
            if (!moved && updates.Count == 1) { Directory.Move(_store.Root, offline); moved = true; }
            else if (moved && !restored && updates.Count == 4)
            {
                Assert.Empty(second.Mount.Starts);
                Directory.Move(offline, _store.Root);
                restored = true;
            }
            else if (restored && !released && updates.Count == 5) { released = true; Release(first, session); }
        };
        try
        {
            AssertStarted(second.Coordinator.LaunchWhenReleased(WaitFor(TimeSpan.FromMinutes(10), updates)));
        }
        finally
        {
            _clock.OnSleep = null;
            if (moved && !restored) Directory.Move(offline, _store.Root);
        }

        var unreachable = updates.Where(u => !u.Fresh).ToList();
        Assert.Equal(3, unreachable.Count);
        Assert.All(unreachable, u => Assert.Equal(FirstPc, u.Owner));
        Assert.Contains("which may be out of date", unreachable[0].Describe(), StringComparison.Ordinal);
        Assert.True(updates[^1].Fresh);
        Assert.Equal(SecondPc, _store.ReadJournal().Owner);
    }

    [Fact]
    public void TimingOutWhileTheServerIsUnreachableIsAStorageWait()
    {
        FirstPcHoldsTheLibrary();
        var second = Computer(SecondPc, "second");
        string offline = _store.Root + "-offline";
        Directory.Move(_store.Root, offline);
        try
        {
            var outcome = second.Coordinator.LaunchWhenReleased(new HandoffWait(TimeSpan.FromSeconds(30)));
            Assert.Equal(LaunchStatus.Waiting, outcome.Status);
            Assert.True(outcome.StorageOffline);
            Assert.StartsWith("Stopped waiting after 30 seconds: the shared library's server could not be reached", outcome.Message, StringComparison.Ordinal);
        }
        finally { Directory.Move(offline, _store.Root); }
        Assert.Empty(second.Mount.Starts);
        Assert.Equal(FirstPc, _store.ReadJournal().Owner);
    }

    [Fact]
    public void WindowsShutdownEndsTheWaitBeforeAnyLaunch()
    {
        var (first, session) = FirstPcHoldsTheLibrary();
        var shutdown = new SessionShutdown(_clock);
        var second = Computer(SecondPc, "second", shutdown: shutdown);
        var updates = new List<HandoffWaitUpdate>();
        _clock.OnSleep = () =>
        {
            if (updates.Count != 1 || shutdown.Ending) return;
            shutdown.RequestEnd();
            // Even a release right after the shutdown signal must not open Medal.
            Release(first, session);
        };

        var outcome = second.Coordinator.LaunchWhenReleased(WaitFor(TimeSpan.FromMinutes(5), updates));
        _clock.OnSleep = null;

        Assert.Equal(LaunchStatus.Waiting, outcome.Status);
        Assert.Equal("Windows is shutting down; Medal was not started.", outcome.Message);
        Assert.Equal(0, second.Medal.Launches);
        Assert.Empty(second.Mount.Starts);
        Assert.Empty(_store.PendingRequests(_clock.Now));
    }

    [Fact]
    public void AnOwnerWaitingForRecoveryEndsTheWaitAtOnce()
    {
        string session = SessionCoordinator.NewId();
        _store.Update(r => Ownership.Acquire(r, FirstPc, session, _clock.Now), TimeSpan.FromSeconds(1));
        _store.Update(r => Ownership.RequireRecovery(r, FirstPc, session, "synthetic interruption", _clock.Now), TimeSpan.FromSeconds(1));
        var second = Computer(SecondPc, "second");
        var updates = new List<HandoffWaitUpdate>();

        var outcome = second.Coordinator.LaunchWhenReleased(WaitFor(TimeSpan.FromMinutes(5), updates));

        Assert.Equal(LaunchStatus.Blocked, outcome.Status);
        Assert.Contains("waiting for recovery on First PC", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(updates);
    }

    [Fact]
    public void APlainLaunchStillReturnsAtOnceAndOffersTheWait()
    {
        FirstPcHoldsTheLibrary();
        var outcome = Computer(SecondPc, "second").Coordinator.Launch();
        Assert.Equal(LaunchStatus.Waiting, outcome.Status);
        Assert.Equal(LaunchWait.OwnedElsewhere, outcome.Wait);
        Assert.Equal(FirstPc, outcome.Journal?.Owner);
        // The plain launch's request stays for the owner to see, as before.
        Assert.Single(_store.PendingRequests(_clock.Now));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("1", 1)]
    [InlineData("3600", 3600)]
    public void WaitForHandoffAcceptsOneSecondToOneHour(string? value, int? seconds) =>
        Assert.Equal(seconds is null ? null : TimeSpan.FromSeconds(seconds.Value), HandoffWait.ParseSeconds(value));

    [Theory]
    [InlineData("0")]
    [InlineData("3601")]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("soon")]
    public void WaitForHandoffRefusesOtherValues(string value) =>
        Assert.Throws<UsageException>(() => HandoffWait.ParseSeconds(value));

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void TheLauncherShowsNoFurtherDialogAfterADeclineOrCancel(bool offered, bool accept, bool keepWaiting, bool settled)
    {
        var ui = new WaitUi(accept ? new View(cancelled: !keepWaiting) : null);
        var tracker = new Launcher.HandoffOfferTracker(ui);
        // Cancellation counts once the wait reads it, as LaunchWhenReleased does between checks.
        if (offered) _ = tracker.Offer(new HandoffWaitOffer("Medal is in use on First PC.", TimeSpan.FromMinutes(15)))?.Cancelled;
        Assert.Equal(settled, tracker.Settled);
    }

    private sealed class View(bool cancelled) : IHandoffWaitView
    {
        public bool Cancelled => cancelled;
        public void Update(HandoffWaitUpdate update) { }
        public void Dispose() { }
    }

    private sealed class WaitUi(IHandoffWaitView? view) : ILauncherUi
    {
        public void Show(string message) => throw new InvalidOperationException("The offer itself carries the message.");
        public void Notify(string message) { }
        public bool ConfirmLibraryChanges(Library.LibraryChangeReview review) => false;
        public IHandoffWaitView? OfferHandoffWait(HandoffWaitOffer offer) => view;
    }
}
