using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MedalSync.Cli;
using MedalSync.Session;

namespace MedalSync.Platform;

/// <summary>
/// The launcher's "wait and open when ready" window: Shell's marquee progress dialog with Cancel. It is created,
/// updated, polled and closed on the launcher's STA thread, which also runs the wait.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsHandoffWaitView : IHandoffWaitView
{
    internal const string FailedMessage =
        "The waiting window closed unexpectedly, so Medal Sync stopped waiting. Medal was not started; open it again after the other PC reaches Ready.";

    private readonly IShellProgressDialog _dialog;
    private readonly Action<string> _show;
    private bool _cancelled;
    private bool _failed;
    private bool _closed;

    /// <param name="show">Tells the user when the window fails, since the wait then stops without its usual message.</param>
    public WindowsHandoffWaitView(TimeSpan timeout, Action<string> show)
    {
        _show = show;
        _dialog = ShellProgressDialog.Create();
        try
        {
            _dialog.SetTitle("Medal Sync");
            _dialog.SetCancelMsg("Stopping the wait. Medal will not be opened.", IntPtr.Zero);
            _dialog.StartProgressDialog(IntPtr.Zero, null, ShellProgressDialog.NoTime | ShellProgressDialog.Marquee, IntPtr.Zero);
            _dialog.SetLine(1, "Waiting for the other PC to hand off the library", false, IntPtr.Zero);
            _dialog.SetLine(3, $"Medal opens here when it is ready. Cancel stops waiting (at most {HandoffWaitUpdate.Duration(timeout)}).", false, IntPtr.Zero);
        }
        catch
        {
            Marshal.FinalReleaseComObject(_dialog);
            throw;
        }
    }

    public void Update(HandoffWaitUpdate update)
    {
        if (_closed || _cancelled) return;
        var (title, detail) = update.Lines();
        try
        {
            _dialog.SetLine(1, title, false, IntPtr.Zero);
            _dialog.SetLine(2, detail, false, IntPtr.Zero);
        }
        catch (Exception)
        {
            // A window that can no longer be shown cannot be cancelled either: stop rather than open Medal unseen.
            Fail();
        }
    }

    public bool Cancelled
    {
        get
        {
            if (_cancelled || _closed) return _cancelled;
            try { _cancelled = _dialog.HasUserCancelled(); }
            catch (Exception) { Fail(); }
            return _cancelled;
        }
    }

    private void Fail() => (_cancelled, _failed) = (true, true);

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        try { _dialog.StopProgressDialog(); }
        catch (Exception) { /* Already gone. */ }
        try { Marshal.FinalReleaseComObject(_dialog); }
        catch (Exception) { /* Released with the process. */ }
        if (_failed) _show(FailedMessage);
    }
}
