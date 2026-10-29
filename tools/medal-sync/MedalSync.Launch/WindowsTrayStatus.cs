using System.Diagnostics;
using MedalSync.Cli;
using MedalSync.Platform;
using MedalSync.Session;

namespace MedalSync.Launch;

/// <summary>A separate, disposable view. The supervisor never depends on this process.</summary>
internal sealed class WindowsTrayStatus : ApplicationContext
{
    private const string MutexName = @"Local\LarsonServer.MedalSync.StatusView";
    private const string OpenEventName = @"Local\LarsonServer.MedalSync.OpenStatus";
    private readonly NotifyIcon _icon;
    private readonly Form _panel;
    private readonly TextBox _status;
    private readonly Button _retry;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly EventWaitHandle _open;
    private Task<DesktopStatus>? _read;
    private DesktopStatus _last = DesktopStatus.Unknown();
    private bool _finishing;
    private bool _ending;
    private readonly Func<DesktopStatus> _readStatus;
    private readonly Func<Task<FinishCommandResult>> _runFinish;
    private string? _finishFailure;

    public static int Run(bool show)
    {
        using var singleton = new Mutex(false, MutexName);
        bool acquired;
        try { acquired = singleton.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            if (show)
            {
                try { using var signal = EventWaitHandle.OpenExisting(OpenEventName); signal.Set(); }
                catch (WaitHandleCannotBeOpenedException) { /* The view is still starting; its tray remains available. */ }
            }
            return 0;
        }
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var view = new WindowsTrayStatus(show);
            Application.Run(view);
            return 0;
        }
        finally { singleton.ReleaseMutex(); }
    }

    internal WindowsTrayStatus(bool show, Func<DesktopStatus>? readStatus = null, string eventName = OpenEventName,
        Func<Task<FinishCommandResult>>? runFinish = null)
    {
        _readStatus = readStatus ?? SessionCommands.ReadDesktopStatus;
        _runFinish = runFinish ?? RunFinish;
        _open = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        _panel = new StatusForm(() => _ending = true)
        {
            Text = "Medal Sync status", AccessibleName = "Medal Sync status",
            StartPosition = FormStartPosition.CenterScreen, MinimumSize = new Size(520, 370), Size = new Size(600, 420),
            AutoScaleMode = AutoScaleMode.Dpi,
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _status = new TextBox
        {
            Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical,
            AccessibleName = "Current status, upload queue, replica, last confirmation and next action", TabIndex = 0,
            BackColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle,
        };
        layout.Controls.Add(_status, 0, 0);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        _retry = new Button { Text = "&Retry finish", AccessibleName = "Retry supported finish on this PC", AutoSize = true, Enabled = false };
        _retry.Click += async (_, _) => await RetryFinish();
        buttons.Controls.Add(_retry);
        var guide = new Button { Text = "Setup &guide", AutoSize = true };
        guide.Click += (_, _) => OpenGuide();
        buttons.Controls.Add(guide);
        var updates = new Button { Text = "Check for &updates", AutoSize = true };
        updates.Click += (_, _) => StartLauncher("--check-updates");
        buttons.Controls.Add(updates);
        var close = new Button { Text = "&Close panel", AutoSize = true, DialogResult = DialogResult.Cancel };
        close.Click += (_, _) => _panel.Hide();
        buttons.Controls.Add(close);
        _panel.CancelButton = close;
        layout.Controls.Add(buttons, 0, 1);
        layout.Controls.Add(new Label { Text = "Closing this panel keeps supervision and uploads running.", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 2);
        _panel.Controls.Add(layout);
        _ = _panel.Handle; // A hidden top-level window must also receive Windows shutdown broadcasts.
        _panel.FormClosing += (_, e) =>
        {
            if (!_ending && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; _panel.Hide(); }
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("&Open status", null, (_, _) => ShowPanel());
        menu.Items.Add("Setup &guide", null, (_, _) => OpenGuide());
        menu.Items.Add("&Configure", null, (_, _) => StartLauncher("--configure"));
        menu.Items.Add("Check for &updates", null, (_, _) => StartLauncher("--check-updates"));
        menu.Items.Add("Exit status &view (supervision continues)", null, (_, _) => ExitThread());
        _icon = new NotifyIcon { Icon = SystemIcons.Application, Text = "Medal Sync: Unknown", Visible = true, ContextMenuStrip = menu };
        _icon.DoubleClick += (_, _) => ShowPanel();
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        Poll();
        if (show) ShowPanel();
    }

    private void Poll()
    {
        if (_ending) { ExitThread(); return; }
        if (_open.WaitOne(0)) ShowPanel();
        if (_read is { IsCompleted: true })
        {
            _last = _read.IsCompletedSuccessfully ? _read.Result : DesktopStatus.Unknown(_last.ConfirmedAt);
            _read = null;
        }
        // At most one read: a hung SMB connection cannot accumulate workers or block the UI.
        _read ??= Task.Run(_readStatus);
        var status = _last.At(DateTimeOffset.UtcNow);
        if (status.Phase is SessionPhase.Ready or SessionPhase.Active or SessionPhase.Waiting) _finishFailure = null;
        string? lastResult = _finishFailure ?? status.LastResult;
        _retry.Enabled = !_finishing && status.CanRetry;
        _icon.Text = "Medal Sync: " + status.Title;
        _icon.Icon = status.Phase switch
        {
            SessionPhase.Ready => SystemIcons.Information,
            SessionPhase.RecoveryNeeded or SessionPhase.Unknown => SystemIcons.Warning,
            _ => SystemIcons.Application,
        };
        string text = $"{status.Title}\r\n\r\nNext action: {status.NextAction}\r\n\r\n"
            + $"Queued files: {status.QueuedFiles?.ToString() ?? "unavailable"}\r\n"
            + $"Queued bytes: {status.QueuedBytes?.ToString() ?? "unavailable"}\r\n"
            + $"Required replica: {status.Replica ?? "none currently reported"}\r\n"
            + $"Last confirmed: {status.ConfirmedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "never"}"
            + (lastResult is null ? "" : $"\r\n\r\nLast finish result: {lastResult}")
            + (status.Notice is null ? "" : $"\r\n\r\nLatest notice: {status.Notice}");
        if (_status.Text != text) _status.Text = text;
    }

    private void ShowPanel()
    {
        if (_ending) return;
        _panel.Show();
        _panel.WindowState = FormWindowState.Normal;
        _panel.Activate();
        _status.Focus();
    }

    private async Task RetryFinish()
    {
        if (_ending || _finishing || !_last.At(DateTimeOffset.UtcNow).CanRetry) return;
        _finishing = true;
        _retry.Enabled = false;
        try
        {
            var result = await _runFinish();
            _finishFailure = result.Failure is { } failure ? $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}: {failure}" : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            _finishFailure = "Could not start finish. Keep this PC's session and cache; check the installation and retry here.";
        }
        finally { _finishing = false; }
    }

    private static async Task<FinishCommandResult> RunFinish()
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "medal-sync.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("finish");
        using var process = Process.Start(start) ?? throw new IOException("Finish did not start.");
        // Preserve the existing single-instance and finish guards in the child. No acceptance flags.
        var output = FinishCommandResult.ReadTailAsync(process.StandardOutput);
        var error = FinishCommandResult.ReadTailAsync(process.StandardError);
        await Task.WhenAll(output, error, process.WaitForExitAsync());
        return new(process.ExitCode, output.Result, error.Result);
    }

    private void OpenGuide()
    {
        if (_ending) return;
        string guide = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "setup.html"));
        string target = File.Exists(guide) ? guide : "https://github.com/Larson-Logan/medal-sync/blob/main/docs/getting-started.md";
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { _status.Text = "Open Medal Sync Setup Guide from the Start menu."; }
    }

    private void StartLauncher(string argument)
    {
        if (_ending) return;
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(argument);
        try { Process.Start(start)?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { _status.Text = "The update check could not start. Check the installation and try again."; }
    }

    protected override void ExitThreadCore()
    {
        _ending = true;
        _timer.Stop();
        _icon.Visible = false;
        _panel.Close();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _icon.ContextMenuStrip?.Dispose(); _icon.Dispose(); _panel.Dispose(); _open.Dispose(); }
        base.Dispose(disposing);
    }

    private sealed class StatusForm(Action ending) : Form
    {
        protected override void WndProc(ref Message message)
        {
            // No prompts/actions during a shutdown query, including a subsequently canceled query.
            if (message.Msg == 0x11) { ending(); message.Result = (IntPtr)1; return; }
            base.WndProc(ref message);
        }
    }
}
