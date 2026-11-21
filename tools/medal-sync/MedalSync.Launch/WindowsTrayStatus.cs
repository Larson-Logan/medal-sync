using System.Diagnostics;
using MedalSync;
using MedalSync.Cli;
using MedalSync.Config;
using MedalSync.Platform;
using MedalSync.Session;

namespace MedalSync.Launch;

/// <summary>A separate, disposable view. The supervisor never depends on this process.</summary>
internal sealed class WindowsTrayStatus : ApplicationContext
{
    private const string MutexName = SetupMaintenance.StatusViewMutexName;
    private const string OpenEventName = @"Local\LarsonServer.MedalSync.OpenStatus";
    private static readonly TimeSpan VisibleReadInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HiddenReadInterval = TimeSpan.FromSeconds(5);
    private readonly NotifyIcon _icon;
    private readonly AppIcons _icons;
    private readonly Form _panel;
    private readonly TextBox _status;
    private readonly Button _retry;
    private readonly Button _retryCloud;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly EventWaitHandle _open;
    private readonly EventWaitHandle _exit;
    private readonly Func<bool> _operationRunning;
    private Task<DesktopStatus>? _read;
    private DateTime _nextRead;
    private DesktopStatus _last = DesktopStatus.Unknown();
    private bool _finishing;
    private bool _retryingCloud;
    private bool _ending;
    private readonly Func<DesktopStatus> _readStatus;
    private readonly Func<Task<FinishCommandResult>> _runFinish;
    private readonly Func<Task<CloudRetryCommandResult>> _runCloudRetry;
    private readonly Action<ProcessStartInfo> _startProcess;
    private string? _cloudRetryResult;
    private string? _finishFailure;
    private string? _exitFailure;
    private string? _actionFailure;
    private readonly Button _signInAgain;
    private readonly Func<string, Task<CloudRetryCommandResult>> _runSignIn;
    private bool _signingIn;
    private string? _signInResult;
    private static readonly TimeSpan PrefetchInterval = TimeSpan.FromMinutes(10);
    private readonly Func<bool> _prefetchDue;
    private readonly Func<Process?> _startPrefetch;
    private readonly Platform.WindowsIdle _idle = new();
    private Process? _prefetch;
    private DateTime _nextPrefetch = DateTime.UtcNow + TimeSpan.FromMinutes(1);
    private readonly DesktopNotifier _notifier = new();
    private readonly Action<DesktopNotification> _showNotification;
    private readonly Action<bool> _saveNotifications;
    private bool _notifications;

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

    // Wait for the view to dispose and release its singleton, rather than only hiding its window.
    // A supervisor is never terminated. Setup repeats its independent readiness check before copying files.
    internal static void CloseForUpdate(string mutexName = MutexName, string eventName = OpenEventName,
        TimeSpan? timeout = null)
    {
        if (!Mutex.TryOpenExisting(mutexName, out var singleton)) return;
        using (singleton)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(eventName + ".Exit");
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                throw new IOException("Close Medal Sync using Exit status view in its tray menu, then retry the installer.");
            }
            bool stopped;
            try { stopped = singleton.WaitOne(timeout ?? TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { stopped = true; }
            if (!stopped) throw new IOException("Medal Sync could not exit. Quit Medal normally, wait for Ready, then choose Exit Medal Sync and retry the installer.");
            singleton.ReleaseMutex();
        }
    }

    internal WindowsTrayStatus(bool show, Func<DesktopStatus>? readStatus = null, string eventName = OpenEventName,
        Func<Task<FinishCommandResult>>? runFinish = null, Func<bool>? operationRunning = null,
        Func<Task<CloudRetryCommandResult>>? runCloudRetry = null, Action<ProcessStartInfo>? startProcess = null,
        Action<DesktopNotification>? showNotification = null, Func<bool>? readNotifications = null, Action<bool>? saveNotifications = null,
        Func<bool>? prefetchDue = null, Func<Process?>? startPrefetch = null,
        Func<string, Task<CloudRetryCommandResult>>? runSignIn = null)
    {
        _runSignIn = runSignIn ?? RunSignIn;
        _prefetchDue = prefetchDue ?? (() => PrefetchEnabled() && _idle.IsIdle());
        _startPrefetch = startPrefetch ?? StartPrefetch;
        _readStatus = readStatus ?? SessionCommands.ReadDesktopStatus;
        _runFinish = runFinish ?? RunFinish;
        _runCloudRetry = runCloudRetry ?? RunCloudRetry;
        _startProcess = startProcess ?? (start => Process.Start(start)?.Dispose());
        _open = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        _exit = new EventWaitHandle(false, EventResetMode.AutoReset, eventName + ".Exit");
        _operationRunning = operationRunning ?? SessionCommands.AnotherInstanceRunning;
        _showNotification = showNotification ?? ShowBalloon;
        _saveNotifications = saveNotifications ?? SaveNotifications;
        _notifications = (readNotifications ?? ReadNotifications)();
        _icons = new AppIcons();
        _panel = new StatusForm(() => _ending = true)
        {
            Text = "Medal Sync status", AccessibleName = "Medal Sync status", Icon = _icons.Window,
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
        _retryCloud = new Button { Text = "Retry &cloud copies", AccessibleName = "Retry pending cloud copies while Ready", AutoSize = true, Enabled = false };
        _retryCloud.Click += async (_, _) => await RetryCloudCopies();
        buttons.Controls.Add(_retryCloud);
        _signInAgain = new Button { Text = "Sign &in again", AccessibleName = "Sign in to cloud storage again in your browser", AutoSize = true, Visible = false, Enabled = false };
        _signInAgain.Click += async (_, _) => await SignInAgain();
        buttons.Controls.Add(_signInAgain);
        var guide = new Button { Text = "Setup &guide", AutoSize = true };
        guide.Click += (_, _) => OpenGuide();
        buttons.Controls.Add(guide);
        var configure = new Button { Text = "&Configure", AccessibleName = "Configure or finish library setup", AutoSize = true };
        configure.Click += (_, _) => StartLauncher("--configure");
        buttons.Controls.Add(configure);
        var updates = new Button { Text = "Check for &updates", AutoSize = true };
        updates.Click += (_, _) => StartLauncher("--check-updates");
        buttons.Controls.Add(updates);
        var close = new Button { Text = "&Close panel", AutoSize = true, DialogResult = DialogResult.Cancel };
        close.Click += (_, _) => _panel.Hide();
        buttons.Controls.Add(close);
        var exit = new Button { Text = "E&xit Medal Sync", AutoSize = true };
        exit.Click += (_, _) => TryExit();
        buttons.Controls.Add(exit);
        _panel.CancelButton = close;
        layout.Controls.Add(buttons, 0, 1);
        layout.Controls.Add(new Label { Text = "Close panel keeps Medal Sync running. Check for updates downloads an update and installs it for you after you quit Medal.", AutoSize = true, MaximumSize = new Size(450, 0), Padding = new Padding(0, 8, 0, 0) }, 0, 2);
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
        var signIn = new ToolStripMenuItem("Start Medal at &sign-in");
        signIn.Click += (_, _) => ToggleSignIn();
        menu.Opening += (_, _) => signIn.Checked = SignInEnabled();
        menu.Items.Add(signIn);
        var notifications = new ToolStripMenuItem("Show &notifications") { CheckOnClick = false };
        notifications.Click += (_, _) => ToggleNotifications();
        menu.Opening += (_, _) => notifications.Checked = _notifications;
        menu.Items.Add(notifications);
        menu.Items.Add("E&xit Medal Sync", null, (_, _) => TryExit());
        _icon = new NotifyIcon { Icon = _icons.For(SessionPhase.Unknown), Text = "Medal Sync: Unknown", Visible = true, ContextMenuStrip = menu };
        _icon.DoubleClick += (_, _) => ShowPanel();
        _icon.BalloonTipClicked += (_, _) => ShowPanel();
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        Poll();
        if (show) ShowPanel();
    }

    private void Poll()
    {
        if (_ending) { ExitThread(); return; }
        if (_exit.WaitOne(0)) { TryExit(); if (_ending) return; }
        if (_open.WaitOne(0)) ShowPanel();
        if (_read is { IsCompleted: true })
        {
            _last = _read.IsCompletedSuccessfully ? _read.Result : DesktopStatus.Unknown(_last.ConfirmedAt);
            _read = null;
            // Once per completed read, never per tick: the notifier only reacts to a changed state.
            Notify(_last.At(DateTimeOffset.UtcNow));
        }
        // At most one read: a hung SMB connection cannot accumulate workers or block the UI. Each read loads the
        // configuration and reaches the share, so it runs every second only while the panel is open. The supervisor's
        // heartbeat stays fresh for 20 seconds, so the slower background pace never shows a live session as stale.
        var now = DateTime.UtcNow;
        if (_read is null && now >= _nextRead)
        {
            _read = Task.Run(_readStatus);
            _nextRead = now + (_panel.Visible ? VisibleReadInterval : HiddenReadInterval);
        }
        var status = _last.At(DateTimeOffset.UtcNow);
        if (status.Phase is SessionPhase.Ready or SessionPhase.Active or SessionPhase.Waiting) _finishFailure = null;
        if (status.Phase == SessionPhase.Ready && !_finishing && !_retryingCloud) _exitFailure = null;
        MaybePrefetch(status);
        _retry.Enabled = !_finishing && !_retryingCloud && status.CanRetry;
        _retryCloud.Enabled = !_finishing && !_retryingCloud && status.Phase == SessionPhase.Ready && status.CanRetryCloudCopies;
        _signInAgain.Visible = status.SignInRemote is not null || _signingIn;
        _signInAgain.Enabled = !_signingIn && !_finishing && !_retryingCloud && CanSignIn(status);
        _icon.Text = "Medal Sync: " + status.Title;
        var icon = _icons.For(status.Phase, status.Recording);
        if (!ReferenceEquals(_icon.Icon, icon)) _icon.Icon = icon;
        RenderStatus(status);
    }

    /// <summary>
    /// Every ten minutes while this PC is idle and Ready, or another PC holds the library, start the background
    /// prefetch. It stops by itself on input, Medal starting, a launch, shutdown or low space; nothing else waits on it.
    /// </summary>
    private void MaybePrefetch(DesktopStatus status)
    {
        if (_prefetch is { } running)
        {
            bool exited;
            try { exited = running.HasExited; }
            catch (InvalidOperationException) { exited = true; }
            if (!exited) return;
            running.Dispose();
            _prefetch = null;
        }
        var now = DateTime.UtcNow;
        if (_ending || _finishing || _retryingCloud || now < _nextPrefetch || status.Phase is not (SessionPhase.Ready or SessionPhase.Waiting)) return;
        _nextPrefetch = now + PrefetchInterval;
        try
        {
            if (!_prefetchDue()) return;
            _prefetch = _startPrefetch();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException
            or InvalidDataException or MedalSyncException) { /* Advisory: launch downloads what it needs. */ }
    }

    private static bool PrefetchEnabled() => Platform.MediaPrefetch.Enabled(Config.MedalSyncConfig.Load(LocalState.ForCurrentUser().ConfigPath));

    private static Process? StartPrefetch()
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "medal-sync.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = false, RedirectStandardError = false };
        start.ArgumentList.Add("prefetch");
        start.ArgumentList.Add("--apply");
        var process = Process.Start(start);
        try { if (process is not null) process.PriorityClass = ProcessPriorityClass.Idle; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* Its downloads already run at low priority. */ }
        return process;
    }

    /// <summary>Exit and shutdown end a running prefetch; it keeps nothing partial in the local copy.</summary>
    private void StopPrefetch()
    {
        if (_prefetch is not { } running) return;
        try { if (!running.HasExited) running.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* Already gone. */ }
        running.Dispose();
        _prefetch = null;
    }

    /// <summary>Advisory: a notification that cannot be shown (Focus Assist, notifications off in Windows) changes nothing.</summary>
    private void Notify(DesktopStatus status)
    {
        var notification = _notifier.Observe(status);
        if (notification is null || !_notifications || _ending) return;
        try { _showNotification(notification); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException) { /* Advisory only. */ }
    }

    private void ShowBalloon(DesktopNotification notification)
    {
        // Windows shows a balloon as a toast; clicking it opens this panel, which offers only the existing actions.
        const int Limit = 255;
        string message = notification.Message.Length <= Limit ? notification.Message : notification.Message[..(Limit - 1)] + "…";
        _icon.ShowBalloonTip(10_000, notification.Title, message,
            notification.Kind == DesktopNotificationKind.Ready ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    private void ToggleNotifications()
    {
        if (_ending) return;
        bool next = !_notifications;
        try
        {
            _saveNotifications(next);
            _notifications = next;
            _actionFailure = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _actionFailure = "The notification setting could not be saved. Try again from the tray menu.";
            ShowPanel();
        }
        RenderStatus(_last.At(DateTimeOffset.UtcNow));
    }

    private static bool ReadNotifications()
    {
        try { return DesktopPreferences.Read(LocalState.ForCurrentUser()).Notifications; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or MedalSyncException) { return true; }
    }

    private static void SaveNotifications(bool on) =>
        (DesktopPreferences.Read(LocalState.ForCurrentUser()) with { Notifications = on }).Save(LocalState.ForCurrentUser());

    private void RenderStatus(DesktopStatus status)
    {
        string? lastResult = _finishFailure ?? status.LastResult;
        string text = $"{status.Title}\r\n\r\nNext action: {status.NextAction}\r\n\r\n"
            + $"Queued files: {status.QueuedFiles?.ToString() ?? "unavailable"}\r\n"
            + $"Queued bytes: {status.QueuedBytes?.ToString() ?? "unavailable"}\r\n"
            + $"Storage copy: {status.Replica ?? "none currently reported"}\r\n"
            + $"Last confirmed: {status.ConfirmedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "never"}"
            + (lastResult is null ? "" : $"\r\n\r\nLast finish result: {lastResult}")
            + (_retryingCloud ? "\r\n\r\nCloud retry: Running. Unfinished copies remain pending."
                : _cloudRetryResult is null ? "" : $"\r\n\r\nLast cloud retry result: {_cloudRetryResult}")
            + (_signingIn ? "\r\n\r\nSign-in: finish signing in in your browser. The credential file changes only after a storage check passes."
                : _signInResult is not null ? $"\r\n\r\nSign-in: {_signInResult}"
                : status.SignInRemote is not null && !CanSignIn(status) ? "\r\n\r\nSign in again is available once Medal is quit and this PC's session has finished."
                : "")
            + (_exitFailure is null ? "" : $"\r\n\r\nExit: {_exitFailure}")
            + (_actionFailure is null ? "" : $"\r\n\r\n{_actionFailure}")
            + (status.Notice is null ? "" : $"\r\n\r\nLatest notice: {status.Notice}");
        if (_status.Text != text) _status.Text = text;
    }

    private void TryExit()
    {
        if (_ending) return;
        if (_finishing || _retryingCloud || _operationRunning())
        {
            _exitFailure = "Medal Sync is still supervising Medal or finishing a library operation. Quit Medal normally from its tray icon and wait for Ready, then choose Exit Medal Sync again. Uploads will continue safely.";
            ShowPanel();
            RenderStatus(_last.At(DateTimeOffset.UtcNow));
            return;
        }
        ExitThread();
    }

    private void ShowPanel()
    {
        if (_ending) return;
        _nextRead = default; // Show current status as soon as the panel opens.
        _panel.Show();
        _panel.WindowState = FormWindowState.Normal;
        _panel.Activate();
        _status.Focus();
    }

    private async Task RetryFinish()
    {
        if (_ending || _finishing || _retryingCloud || !_last.At(DateTimeOffset.UtcNow).CanRetry) return;
        _finishing = true;
        _retry.Enabled = false;
        _retryCloud.Enabled = false;
        try
        {
            var result = await _runFinish();
            _finishFailure = result.Failure is { } failure ? $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}: {failure}" : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            _finishFailure = "Could not start finish. Keep this PC's session and cache; check the installation and retry here.";
        }
        finally { _finishing = false; _nextRead = default; }
    }

    private static async Task<FinishCommandResult> RunFinish()
    {
        var result = await RunCommand("finish");
        return new(result.ExitCode, result.Output, result.Error);
    }

    private async Task RetryCloudCopies()
    {
        var status = _last.At(DateTimeOffset.UtcNow);
        if (_ending || _finishing || _retryingCloud || status.Phase != SessionPhase.Ready || !status.CanRetryCloudCopies) return;
        _retryingCloud = true;
        _cloudRetryResult = null;
        _retryCloud.Enabled = false;
        _retry.Enabled = false;
        try { _cloudRetryResult = (await _runCloudRetry()).Message; }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            _cloudRetryResult = "Could not start cloud retry. Copies remain pending; check the installation and retry while Ready.";
        }
        finally { _retryingCloud = false; _nextRead = default; }
    }

    /// <summary>rclone's sign-in page needs its local port and a stable credential file, so no session may run here.</summary>
    private static bool CanSignIn(DesktopStatus status) => status.SignInRemote is not null && status.Phase is SessionPhase.Ready or SessionPhase.Waiting;

    private async Task SignInAgain()
    {
        var status = _last.At(DateTimeOffset.UtcNow);
        if (_ending || _signingIn || _finishing || _retryingCloud || !CanSignIn(status)) return;
        _signingIn = true;
        _signInResult = null;
        _signInAgain.Enabled = false;
        RenderStatus(status);
        try
        {
            var result = await _runSignIn(status.SignInRemote!);
            _signInResult = result.ExitCode == 0
                ? "Signed in again; the storage check passed."
                : "Not signed in. " + (string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim());
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            _signInResult = "Sign-in could not start. Check the installation and try again.";
        }
        finally { _signingIn = false; _nextRead = default; }
    }

    private static async Task<CloudRetryCommandResult> RunSignIn(string remote)
    {
        var result = await RunCommand("storage", "sign-in", "--remote", remote);
        return new(result.ExitCode, result.Output, result.Error);
    }

    private static async Task<CloudRetryCommandResult> RunCloudRetry()
    {
        var result = await RunCommand("replicate", "retry", "--apply");
        return new(result.ExitCode, result.Output, result.Error);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCommand(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "medal-sync.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("The library operation did not start.");
        // Preserve each command's existing interlock, shutdown and ownership guards. No acceptance flags.
        var output = FinishCommandResult.ReadTailAsync(process.StandardOutput);
        var error = FinishCommandResult.ReadTailAsync(process.StandardError);
        await Task.WhenAll(output, error, process.WaitForExitAsync());
        return (process.ExitCode, output.Result, error.Result);
    }

    private void OpenGuide()
    {
        if (_ending) return;
        string guide = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "setup.html"));
        string target = File.Exists(guide) ? guide : "https://github.com/Larson-Logan/medal-sync/blob/main/docs/getting-started.md";
        StartAction(new ProcessStartInfo(target) { UseShellExecute = true },
            "The setup guide could not open. Open Medal Sync Setup Guide from the Start menu.");
    }

    private void StartLauncher(string argument)
    {
        if (_ending) return;
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(argument);
        string action = argument == "--configure" ? "Configuration" : "The update check";
        StartAction(start, $"{action} could not start. Check the installation and try again.");
    }

    private void StartAction(ProcessStartInfo start, string failure)
    {
        try
        {
            _startProcess(start);
            _actionFailure = null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            _actionFailure = failure;
            ShowPanel();
        }
        RenderStatus(_last.At(DateTimeOffset.UtcNow));
    }

    private static StartupIntegration OpenStartup() =>
        StartupIntegration.ForCurrentUser(LocalState.ForCurrentUser().Root, Path.Combine(AppContext.BaseDirectory, StartupEntries.LauncherFile));

    private static bool SignInEnabled()
    {
        try { using var startup = OpenStartup(); return startup.Enabled; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }

    /// <summary>Turns starting Medal through Medal Sync on or off after showing exactly what changes.</summary>
    private void ToggleSignIn()
    {
        if (_ending) return;
        const string Title = "Medal Sync";
        try
        {
            using var startup = OpenStartup();
            bool on = startup.Enabled;
            string question = on
                ? "Stop starting Medal through Medal Sync?\n\nMedal's own startup entries and shortcuts are put back. Medal then starts without its clip drive unless you open it through Medal Sync."
                : "Start Medal through Medal Sync when you sign in?\n\n" + string.Join("\n", startup.Preview().Select(line => "• " + line))
                  + "\n\nMedal's entries are saved first, and you can turn this off again from this menu.";
            if (MessageBox.Show(question, Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var done = on ? startup.Restore() : startup.Apply();
            string result = on ? "Medal starts with its own entries again." : "Medal now starts through Medal Sync when you sign in.";
            bool warn = !on && startup.TurnedOffInWindows;
            MessageBox.Show(result + (warn ? "\n\n" + StartupIntegration.TurnedOffMessage : ""), Title, MessageBoxButtons.OK,
                warn ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is MedalSyncException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void ExitThreadCore()
    {
        _ending = true;
        StopPrefetch();
        _timer.Stop();
        _icon.Visible = false;
        _panel.Close();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _icon.ContextMenuStrip?.Dispose(); _icon.Dispose(); _panel.Dispose(); _icons.Dispose(); _open.Dispose(); _exit.Dispose(); }
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
