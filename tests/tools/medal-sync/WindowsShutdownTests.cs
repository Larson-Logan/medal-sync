using System.Diagnostics;
using System.Runtime.InteropServices;
using MedalSync.Platform;
using MedalSync.Session;

namespace MedalSync.Tests;

public sealed class WindowsShutdownTests
{
    [Fact]
    public void QueryAndCancellationDoNotRequestExitOrPreventLaterLaunch()
    {
        var state = new SessionShutdown(new SystemClock());
        state.Query();
        Assert.True(state.SuppressUi);
        Assert.False(state.Ending);
        Assert.False(state.TryLaunch(() => Assert.Fail("Do not start a new app while Windows is querying shutdown")));
        state.CancelQuery();
        Assert.False(state.SuppressUi);
        bool launched = false;
        Assert.True(state.TryLaunch(() => launched = true));
        Assert.True(launched);
    }

    [Fact]
    public void ConfirmedShutdownIsIrreversibleAndPreventsAnotherLaunch()
    {
        var state = new SessionShutdown(new SystemClock());
        state.RequestEnd();
        state.CancelQuery();
        Assert.True(state.Ending);
        Assert.True(state.SuppressUi);
        Assert.False(state.TryLaunch(() => Assert.Fail()));
        state.Complete();
        Assert.True(state.WaitForCompletion());
    }

    [Fact]
    public async Task EndRequestWakesSupervisorWithoutWaitingForItsNormalPoll()
    {
        var state = new SessionShutdown(new SystemClock());
        var worker = Task.Run(() => state.Pause(TimeSpan.FromSeconds(30)), TestContext.Current.CancellationToken);
        state.RequestEnd();
        await worker.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void NativeHiddenTopLevelWindowReceivesQueryAndCancellation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var state = new SessionShutdown(new SystemClock());
        using var hook = new WindowsSessionEnd(state, _ => { }, setOrder: false);
        Assert.NotEqual(IntPtr.Zero, hook.Window);
        Assert.Equal(IntPtr.Zero, GetParent(hook.Window));
        Assert.False(IsWindowVisible(hook.Window));
        Assert.Equal(new IntPtr(1), Send(hook.Window, WindowsSessionEnd.QueryEndSession, false));
        Assert.True(state.SuppressUi);
        Assert.False(state.Ending);
        Send(hook.Window, WindowsSessionEnd.EndSession, false);
        Assert.False(state.SuppressUi);
        Assert.False(state.Ending);
    }

    [Theory]
    [InlineData(0L)] // shutdown/restart
    [InlineData(0x80000000L)] // logoff
    [InlineData(0x40000000L)] // critical/forced: still never accept deletions
    [InlineData(1L)] // Restart Manager
    public void NativeConfirmationWaitsForWorkerAndAllowsWindowsToContinue(long flags)
    {
        if (!OperatingSystem.IsWindows()) return;
        var state = new SessionShutdown(new SystemClock());
        using var hook = new WindowsSessionEnd(state, _ => { }, setOrder: false);
        state.Complete();
        // Send only to our synthetic hook. No broadcast, ExitWindowsEx, real
        // Medal process or machine shutdown is used by this test.
        Assert.Equal(IntPtr.Zero, Send(hook.Window, WindowsSessionEnd.EndSession, true, flags));
        Assert.True(state.Ending);
    }

    [Fact]
    public void NativeUnfinishedWorkerHasBoundedWaitAndRetainsRecoveryState()
    {
        if (!OperatingSystem.IsWindows()) return;
        var state = new SessionShutdown(new SystemClock(), TimeSpan.FromMilliseconds(80));
        using var hook = new WindowsSessionEnd(state, _ => { }, setOrder: false);
        var timer = Stopwatch.StartNew();
        Send(hook.Window, WindowsSessionEnd.QueryEndSession, false);
        Send(hook.Window, WindowsSessionEnd.EndSession, true);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3));
        Assert.True(state.Ending);
        // The native timed wait can return just before the UTC deadline due
        // to timer rounding. Assert eventual expiry without a scheduling race.
        Assert.True(SpinWait.SpinUntil(() =>
        {
            try { state.RequireTime(); return false; }
            catch (OperationCanceledException) { return true; }
        }, TimeSpan.FromSeconds(1)));
        Assert.Throws<OperationCanceledException>(state.RequireTime);
    }

    [Fact]
    public async Task NativeHandlerWaitsUntilTheExistingSupervisorCompletes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var state = new SessionShutdown(new SystemClock());
        using var hook = new WindowsSessionEnd(state, _ => { }, setOrder: false);
        IntPtr window = hook.Window;
        uint endSession = WindowsSessionEnd.EndSession;
        var handler = Task.Run(() => Send(window, endSession, true), TestContext.Current.CancellationToken);
        var deadline = Stopwatch.StartNew();
        while (!state.Ending && deadline.Elapsed < TimeSpan.FromSeconds(2))
            await Task.Delay(10, TestContext.Current.CancellationToken);
        try
        {
            Assert.True(state.Ending);
            Assert.False(handler.IsCompleted);
        }
        finally { state.Complete(); }
        await handler.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void LoggingFailureCannotEscapeNativeShutdownCallback()
    {
        if (!OperatingSystem.IsWindows()) return;
        var state = new SessionShutdown(new SystemClock());
        using var hook = new WindowsSessionEnd(state, _ => throw new IOException("unwritable local log"), setOrder: false);
        Send(hook.Window, WindowsSessionEnd.QueryEndSession, false);
        Send(hook.Window, WindowsSessionEnd.EndSession, false);
        Assert.False(state.SuppressUi);
    }

    private static IntPtr Send(IntPtr window, uint message, bool ending, long flags = 0)
    {
        Assert.NotEqual(IntPtr.Zero, SendMessageTimeoutW(window, message, ending ? new UIntPtr(1) : UIntPtr.Zero,
            new IntPtr(flags), 2, 4000, out var result));
        return result;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);
}
