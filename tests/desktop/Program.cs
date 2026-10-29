using System.Reflection;
using System.Runtime.InteropServices;
using MedalSync.Launch;
using MedalSync.Platform;
using MedalSync.Session;

internal static class Program
{
    private static int _assertions;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _assertions++;
    }

    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var gate = new object();
        DesktopStatus current = new(SessionPhase.Starting, "Synthetic startup", DateTimeOffset.UtcNow);
        bool disconnected = false;
        int reads = 0;
        int finishes = 0;
        FinishCommandResult finishResult = new(1, "finish: Blocked: Removed clips need review. Run audit and repair before retrying.", "");
        DesktopStatus Read()
        {
            lock (gate)
            {
                reads++;
                if (disconnected) throw new IOException("Synthetic disconnect");
                return current;
            }
        }
        using var view = new WindowsTrayStatus(true, Read, @"Local\MedalSync.DesktopTest." + Guid.NewGuid().ToString("N"),
            () => { finishes++; return Task.FromResult(finishResult); });
        var panel = Field<Form>(view, "_panel");
        var status = Field<TextBox>(view, "_status");
        var retry = Field<Button>(view, "_retry");
        var icon = Field<NotifyIcon>(view, "_icon");
        foreach (SessionPhase phase in Enum.GetValues<SessionPhase>())
        {
            lock (gate) current = new(phase, "Synthetic next action " + phase, DateTimeOffset.UtcNow, 3, 1024, "synthetic-replica", phase == SessionPhase.RecoveryNeeded);
            Until(view, () => status.Text.Contains("Next action: Synthetic next action " + phase + "\r\n", StringComparison.Ordinal));
            Check(status.Text.Contains("Queued files: 3", StringComparison.Ordinal), "Queue files are readable");
            Check(status.Text.Contains("Queued bytes: 1024", StringComparison.Ordinal), "Queue bytes are readable");
            Check(status.Text.Contains("synthetic-replica", StringComparison.Ordinal), "Replica is readable");
            Check(status.Text.Contains("Last confirmed:", StringComparison.Ordinal), "Confirmation time is readable");
            Check(retry.Enabled == (phase == SessionPhase.RecoveryNeeded), "Only supported recovery enables retry");
        }
        Check(status.ReadOnly && status.TabStop && status.AccessibleName!.Contains("next action", StringComparison.Ordinal), "Status supports keyboard and accessibility APIs");
        Check(panel.CancelButton is Button, "Escape closes the panel");
        Check(icon.BalloonTipText.Length == 0, "Background status never shows a balloon");
        lock (gate) current = new(SessionPhase.RecoveryNeeded, "Retry after recovery", DateTimeOffset.UtcNow, CanRetry: true);
        Until(view, () => retry.Enabled);
        retry.PerformClick();
        Until(view, () => status.Text.Contains("Run audit and repair before retrying.", StringComparison.Ordinal) && retry.Enabled);
        Check(finishes == 1, "A blocked finish exposes its diagnostic and required recovery action");
        finishResult = new(0, "Ready", "");
        retry.PerformClick();
        Until(view, () => !status.Text.Contains("Last finish result:", StringComparison.Ordinal));
        Check(finishes == 2, "A successful retry clears the previous refusal");
        lock (gate) disconnected = true;
        Until(view, () => status.Text.StartsWith("Unknown / unreachable", StringComparison.Ordinal) && !retry.Enabled);
        Check(status.Text.Contains("Last confirmed:", StringComparison.Ordinal), "Disconnect retains the last confirmation");
        lock (gate) { disconnected = false; current = new(SessionPhase.Ready, "Synthetic handoff completed", DateTimeOffset.UtcNow); }
        Until(view, () => status.Text.StartsWith("Ready\r\n", StringComparison.Ordinal));
        lock (gate) current = current with { ConfirmedAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        Until(view, () => status.Text.StartsWith("Unknown / unreachable", StringComparison.Ordinal));
        int before = reads;
        panel.Close();
        Check(!panel.Visible && !panel.IsDisposed, "Closing hides the panel without ending the view");
        Until(view, () => reads > before);
        Check(icon.Visible, "Status and reconnect continue while the panel is closed");
        _ = SendMessageW(panel.Handle, 0x11, IntPtr.Zero, IntPtr.Zero);
        Check(Field<bool>(view, "_ending"), "Shutdown suppresses further interactive actions even for a hidden panel");
        // A view is disposable without invoking any session, rclone or library operation.
        Console.WriteLine($"PASS: {_assertions} native desktop assertions with a fake supervisor");
        return 0;
    }

    private static T Field<T>(object view, string name) => (T)view.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    private static void Until(WindowsTrayStatus view, Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        do
        {
            view.GetType().GetMethod("Poll", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, null);
            Application.DoEvents();
            if (ready()) return;
            Thread.Sleep(10);
        } while (DateTime.UtcNow < until);
        throw new TimeoutException("Native status did not update from the fake supervisor.");
    }
}
