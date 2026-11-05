using MedalSync.Config;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Tests.Fixtures;

namespace MedalSync.Tests;

/// <summary>A published session's cache and login: a failed delete leaves a receipted leftover that blocks nothing and is retried.</summary>
public sealed class FinishedSessionCleanupTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Library = new('a', 32);
    private readonly MedalFixture _fixture = new();
    private readonly LocalState _state;

    public FinishedSessionCleanupTests() => _state = new(_fixture.PathFor("state"));

    public void Dispose() => _fixture.Dispose();

    private string Cache(string session) => _fixture.PathFor("cache", Library, session);

    private string Login(string session) => Path.Combine(_state.Root, "sessions", session + ".json");

    private string Finished(string session) => Path.Combine(_state.Root, "finished-sessions", session);

    /// <summary>A drained session as it stands when finish removes it: a login and a cache holding uploaded clips.</summary>
    private string Session()
    {
        string session = SessionCoordinator.NewId();
        _ = _state.SessionSecret(session, Now);
        Directory.CreateDirectory(Cache(session));
        File.WriteAllText(Path.Combine(Cache(session), "uploaded.mp4"), "already on the server");
        return session;
    }

    private static void Locked(string path) => throw new IOException($"Synthetic: another program holds a file in {path}.");

    [Fact]
    public void AFailedCacheDeleteLeavesAReceiptedLeftoverThatDoesNotBlockSetupAndIsRetried()
    {
        string session = Session();
        Assert.Throws<UnsafeOperationException>(() => SetupMaintenance.RequireReady(_state.Root, false, false));

        Assert.Throws<IOException>(() => FinishedSessionCleanup.Forget(_state, Library, session, Cache(session), 7, Now, Locked));

        // The login left sessions/ before the delete, beside a receipt naming the committed generation.
        Assert.False(File.Exists(Login(session)));
        Assert.True(File.Exists(Path.Combine(Finished(session), "login.json")));
        var receipt = _state.ReadFinished(session);
        Assert.Equal((Library, Cache(session), 7L), (receipt.LibraryId, receipt.Cache, receipt.Generation));
        Assert.True(File.Exists(Path.Combine(Cache(session), "uploaded.mp4")));
        SetupMaintenance.RequireReady(_state.Root, false, false);

        // A retry that still cannot delete reports it and does not throw.
        var left = FinishedSessionCleanup.Retry(_state, Library, Cache, deleteDirectory: Locked);
        Assert.Contains(session, Assert.Single(left), StringComparison.Ordinal);
        Assert.True(Directory.Exists(Finished(session)));

        Assert.Empty(FinishedSessionCleanup.Retry(_state, Library, Cache));
        Assert.False(Directory.Exists(Cache(session)));
        Assert.False(Directory.Exists(Finished(session)));
        Assert.Empty(_state.FinishedSessionIds());
    }

    [Fact]
    public void ASuccessfulForgetLeavesNothingBehind()
    {
        string session = Session();
        FinishedSessionCleanup.Forget(_state, Library, session, Cache(session), 3, Now);
        Assert.False(Directory.Exists(Cache(session)));
        Assert.False(File.Exists(Login(session)));
        Assert.False(Directory.Exists(Finished(session)));
        SetupMaintenance.RequireReady(_state.Root, false, false);
    }

    [Fact]
    public void RetryFinishesAMoveThatACrashInterrupted()
    {
        string session = Session();
        // Crash after the receipt was written, before the login moved.
        _state.RetireSession(session, Library, Cache(session), 4, Now);
        File.Move(Path.Combine(Finished(session), "login.json"), Login(session));
        Assert.Throws<UnsafeOperationException>(() => SetupMaintenance.RequireReady(_state.Root, false, false));

        Assert.Empty(FinishedSessionCleanup.Retry(_state, Library, Cache));
        Assert.False(File.Exists(Login(session)));
        Assert.False(Directory.Exists(Cache(session)));
        SetupMaintenance.RequireReady(_state.Root, false, false);
    }

    [Fact]
    public void RetryTouchesOnlyReceiptedSessionsOfThisLibraryAndTheirConfiguredCache()
    {
        // No receipt: possibly undrained, so it is never touched and still blocks setup.
        string unfinished = Session();
        // Another library's receipt is left for that library's configuration.
        string other = Session();
        Assert.Throws<IOException>(() => FinishedSessionCleanup.Forget(_state, new string('c', 32), other, Cache(other), 1, Now, Locked));
        // A receipt naming a folder the configuration does not name is kept and reported.
        string moved = Session();
        Assert.Throws<IOException>(() => FinishedSessionCleanup.Forget(_state, Library, moved, Cache(moved), 2, Now, Locked));

        var left = FinishedSessionCleanup.Retry(_state, Library, s => s == moved ? _fixture.PathFor("elsewhere", s) : Cache(s));

        Assert.Contains(moved, Assert.Single(left), StringComparison.Ordinal);
        Assert.True(File.Exists(Login(unfinished)));
        Assert.True(File.Exists(Path.Combine(Cache(unfinished), "uploaded.mp4")));
        Assert.True(Directory.Exists(Cache(other)));
        Assert.True(Directory.Exists(Finished(other)));
        Assert.True(Directory.Exists(Cache(moved)));
        Assert.Throws<UnsafeOperationException>(() => SetupMaintenance.RequireReady(_state.Root, false, false));
        Assert.Equal(new[] { other, moved }.Order(StringComparer.Ordinal), _state.FinishedSessionIds());
    }

    [Fact]
    public void AnEditedOrMismatchedReceiptIsKept()
    {
        string session = Session();
        Assert.Throws<IOException>(() => FinishedSessionCleanup.Forget(_state, Library, session, Cache(session), 5, Now, Locked));
        Assert.Throws<InvalidDataException>(() => _state.RetireSession(session, Library, Cache(session), 6, Now));
        File.WriteAllText(Path.Combine(Finished(session), LocalState.FinishedReceiptName), "{");
        Assert.Single(FinishedSessionCleanup.Retry(_state, Library, Cache));
        Assert.True(Directory.Exists(Cache(session)));
        Assert.True(File.Exists(Path.Combine(Finished(session), "login.json")));
    }

    [Fact]
    public void AnUnexpectedFileKeepsTheReceiptSoALaterRetryStillWorks()
    {
        string session = Session();
        Assert.Throws<IOException>(() => FinishedSessionCleanup.Forget(_state, Library, session, Cache(session), 8, Now, Locked));
        string stray = Path.Combine(Finished(session), "notes.txt");
        File.WriteAllText(stray, "not Medal Sync's");

        Assert.Contains(session, Assert.Single(FinishedSessionCleanup.Retry(_state, Library, Cache)), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Finished(session), LocalState.FinishedReceiptName)));
        Assert.Equal(8, _state.ReadFinished(session).Generation);
        Assert.True(File.Exists(stray));

        File.Delete(stray);
        Assert.Empty(FinishedSessionCleanup.Retry(_state, Library, Cache));
        Assert.False(Directory.Exists(Finished(session)));
    }

    [Fact]
    public void AnEmptyFolderLeftByAFailedFolderRemovalIsRemovedOnRetry()
    {
        string session = SessionCoordinator.NewId();
        Directory.CreateDirectory(Finished(session));
        Assert.Empty(FinishedSessionCleanup.Retry(_state, Library, Cache));
        Assert.False(Directory.Exists(Finished(session)));
        Assert.Empty(_state.FinishedSessionIds());
    }
}
