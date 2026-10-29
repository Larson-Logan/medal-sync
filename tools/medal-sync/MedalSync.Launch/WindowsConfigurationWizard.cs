using MedalSync.Cli;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Platform;

namespace MedalSync.Launch;

/// <summary>Configuration only. Enrollment, provider authorization and startup remain explicit operations.</summary>
internal sealed class WindowsConfigurationWizard : Form
{
    private readonly LocalState _state;
    private readonly MedalSyncConfig? _existing;
    private readonly Action<MedalSyncConfig, bool> _validate;
    private readonly Action<MedalSyncConfig> _validateExisting;
    private readonly Func<IDisposable> _acquireGuard;
    private readonly Dictionary<string, TextBox> _fields = [];
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Storage mode" };
    private readonly ComboBox _identity = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "New or existing library" };
    private readonly NumericUpDown _cacheLimit = new() { Minimum = 1, Maximum = 100000, Value = 20, AccessibleName = "Cache limit in GiB" };
    private readonly NumericUpDown _reserve = new() { Minimum = 1, Maximum = 100000, Value = 20, AccessibleName = "Minimum free space in GiB" };
    private readonly NumericUpDown _hours = new() { Minimum = 1, Maximum = 24, Value = 4, AccessibleName = "Maximum session hours" };
    private readonly Panel _edit;
    private readonly TextBox _review = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AccessibleName = "Configuration review and next steps", Visible = false };
    private readonly Button _next = new() { Text = "&Review", AutoSize = true };
    private readonly Button _save = new() { Text = "&Confirm and save", AutoSize = true, Enabled = false };
    private readonly Button _back = new() { Text = "&Back", AutoSize = true, Enabled = false };
    private readonly Label _message = new() { AutoSize = true, MaximumSize = new Size(750, 0), AccessibleName = "Setup result" };
    private ConfigurationReview? _proposal;
    private bool _busy;
    private bool _ending;

    public static int Run(LocalState state)
    {
        Application.EnableVisualStyles();
        try
        {
            using var wizard = new WindowsConfigurationWizard(state);
            return wizard.ShowDialog() == DialogResult.OK ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or MedalSyncException)
        {
            MessageBox.Show(ex.Message + "\r\nRecover the existing configuration at " + state.ConfigPath + " before trying again.",
                "Medal Sync configuration", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }

    internal WindowsConfigurationWizard(LocalState state, Action<MedalSyncConfig, bool>? validate = null, Func<IDisposable>? acquireGuard = null)
    {
        _state = state;
        _validate = validate ?? ValidateEnvironment;
        _validateExisting = validate is null ? RequireReady : config => validate(config, false);
        _acquireGuard = acquireGuard ?? AcquireGuard;
        _existing = File.Exists(state.ConfigPath) ? MedalSyncConfig.Load(state.ConfigPath) : null;
        Text = "Medal Sync configuration";
        AccessibleName = Text;
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(850, 760);
        MinimumSize = new Size(650, 520);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 4, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(750, 0), Text = $"Resolved configuration: {state.ConfigPath}\r\n"
            + (_existing is null ? "Prepare a configuration, review it, then confirm saving. Enrollment is a separate step."
                : "Existing setup found. Account, library and device identities are preserved. Quit Medal and wait for Ready before changing it.") }, 0, 0);
        _edit = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 12, 12, 12) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _mode.Items.AddRange(["Server (SMB)", "Local storage"]);
        _mode.SelectedIndex = _existing?.StorageMode == StorageMode.Local ? 1 : 0;
        Add(fields, "Storage &mode", _mode);
        _identity.Items.AddRange(["Create a new library ID", "Join an existing library ID"]);
        _identity.SelectedIndex = _existing is null ? 0 : 1;
        _identity.Enabled = _existing is null;
        Add(fields, "Library &choice", _identity);
        Field(fields, "library", "&Existing library ID", _existing?.LibraryId ?? "");
        _fields["library"].ReadOnly = _existing is not null;
        _fields["library"].Enabled = _identity.SelectedIndex == 1;
        _identity.SelectedIndexChanged += (_, _) => _fields["library"].Enabled = _identity.SelectedIndex == 1;
        Field(fields, "account", "Medal &account ID", _existing?.AccountId ?? "");
        _fields["account"].ReadOnly = _existing is not null;
        Field(fields, "remote", "&Storage folder", _existing?.Remote ?? "");
        Field(fields, "coordination", "&Coordination folder", _existing?.CoordinationRoot ?? Path.Combine(state.Root, "coordination"));
        Field(fields, "capture", "Capture folder / &drive", _existing?.CaptureFolder ?? @"M:\Clips");
        Field(fields, "cache", "SSD cac&he folder", _existing?.CacheRoot ?? "");
        Field(fields, "rclone", "&Rclone executable", _existing?.RclonePath ?? ConfigCommands.DefaultRclonePath());
        Field(fields, "copy", "Persistent local copy (optional)", _existing?.Replication?.LocalCopyRoot ?? "");
        Field(fields, "cloudConfig", "Provider config file (optional)", _existing?.Replication?.RcloneConfigPath ?? "");
        Field(fields, "targets", "Cloud targets: name=remote (one per line)", string.Join(Environment.NewLine, _existing?.Replication?.Targets.Select(t => t.Name + "=" + t.Remote) ?? []), true);
        Field(fields, "syncFolder", "Sync folder ID (optional)", _existing?.SyncExclusions.FirstOrDefault()?.FolderId ?? "");
        Field(fields, "syncPattern", "Sync exclusion pattern", _existing?.SyncExclusions.FirstOrDefault()?.Pattern ?? "/Videos/Clips");
        _cacheLimit.Value = _existing?.Mount.CacheMaxGiB ?? 20;
        _reserve.Value = _existing?.Mount.MinFreeGiB ?? 20;
        _hours.Value = (decimal)(_existing?.MaxSessionHours ?? 4);
        Add(fields, "Cache limit (GiB)", _cacheLimit);
        Add(fields, "Minimum free space (GiB)", _reserve);
        Add(fields, "Maximum session hours", _hours);
        _edit.Controls.Add(fields);
        var content = new Panel { Dock = DockStyle.Fill };
        content.Controls.Add(_edit);
        content.Controls.Add(_review);
        layout.Controls.Add(content, 0, 1);
        layout.Controls.Add(_message, 0, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        var cancel = new Button { Text = "Ca&ncel", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange([_back, _next, _save, cancel]);
        CancelButton = cancel;
        AcceptButton = _next;
        _next.Click += async (_, _) => await Review();
        _back.Click += (_, _) => { _proposal = null; _review.Visible = false; _edit.Visible = true; _next.Enabled = true; _save.Enabled = _back.Enabled = false; AcceptButton = _next; };
        _save.Click += async (_, _) => await Save();
        FormClosing += (_, e) => { if (_busy && e.CloseReason == CloseReason.UserClosing) e.Cancel = true; };
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
    }

    private static void Add(TableLayoutPanel table, string label, Control control)
    {
        int row = table.RowCount++;
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(3, 5, 3, 5);
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, UseMnemonic = true }, 0, row);
        table.Controls.Add(control, 1, row);
    }

    private void Field(TableLayoutPanel table, string key, string label, string value, bool multiline = false)
    {
        var text = new TextBox { Text = value, AccessibleName = label.Replace("&", "", StringComparison.Ordinal), Multiline = multiline };
        if (multiline) { text.Height = 55; text.ScrollBars = ScrollBars.Vertical; }
        _fields.Add(key, text);
        Add(table, label, text);
    }

    internal MedalSyncConfig Candidate()
    {
        string Value(string key) => _fields[key].Text.Trim();
        string? Optional(string key) => Value(key) is { Length: > 0 } value ? value : null;
        bool fresh = _identity.SelectedIndex == 0;
        string library = fresh ? Guid.NewGuid().ToString("N") : Value("library");
        if (string.IsNullOrEmpty(library)) throw new InvalidDataException("Enter the existing library ID to join; obtain it from the configured PC.");
        if (string.IsNullOrEmpty(Value("account")) || Value("account").All(c => c == '0'))
            throw new InvalidDataException("Enter your numeric Medal account ID from 'medal-sync inspect'.");
        var targets = new List<ReplicaTarget>();
        foreach (string line in Value("targets").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] pair = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2) throw new InvalidDataException("Each cloud target must be name=remote:folder, one per line.");
            targets.Add(new(pair[0], pair[1]));
        }
        var replication = _existing?.Replication ?? new ReplicationConfig(null, null, []);
        replication = replication with { LocalCopyRoot = Optional("copy"), RcloneConfigPath = Optional("cloudConfig"), Targets = targets };
        var exclusions = _existing?.SyncExclusions.Skip(1).ToList() ?? [];
        if (Optional("syncFolder") is { } folder) exclusions.Insert(0, new(folder, Value("syncPattern")));
        return new(1, library, Value("account"), Value("coordination"), Value("remote"), Value("capture"), Value("cache"), Value("rclone"),
            (_existing?.Mount ?? new MountConfig(5, 20, 20, 300, 30, 1000, 2, 53682)) with { CacheMaxGiB = (int)_cacheLimit.Value, MinFreeGiB = (int)_reserve.Value },
            (double)_hours.Value, exclusions)
        {
            StorageMode = _mode.SelectedIndex == 1 ? StorageMode.Local : StorageMode.Server,
            Replication = replication.LocalCopyRoot is null && replication.RcloneConfigPath is null && targets.Count == 0 ? null : replication,
        };
    }

    private async Task Review()
    {
        if (_busy || _ending) return;
        await Work(async () =>
        {
            var candidate = Candidate();
            bool fresh = _identity.SelectedIndex == 0;
            _proposal = await Task.Run(() => { using var guard = _acquireGuard(); return ConfigurationReview.Prepare(_state, candidate, fresh, _validate, _validateExisting); });
            if (_ending) return;
            _review.Text = _proposal.Text;
            _edit.Visible = false;
            _review.Visible = true;
            _review.BringToFront();
            _review.Focus();
            _save.Enabled = _back.Enabled = true;
            AcceptButton = null; // Enter in the review never silently saves.
        });
    }

    private async Task Save()
    {
        if (_busy || _ending || _proposal is null) return;
        await Work(async () =>
        {
            string? backup = await Task.Run(() => { using var guard = _acquireGuard(); return _proposal.Save(true, _validate, _validateExisting); });
            if (_ending) return;
            MessageBox.Show(this, "Configuration saved and verified.\r\n" + (backup is null ? "" : $"Previous configuration: {backup}\r\n")
                + "Follow the reviewed initialization or enrollment steps in Medal Sync Setup Guide before launching Medal.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            _busy = false;
            DialogResult = DialogResult.OK;
            Close();
        });
    }

    private async Task Work(Func<Task> work)
    {
        _busy = true;
        _next.Enabled = _save.Enabled = _back.Enabled = false;
        _message.Text = "Checking configuration, compatibility and storage. No library is being initialized.";
        try { await work(); if (!_ending) _message.Text = "Review all destinations and next steps, then explicitly confirm saving."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or MedalSyncException or ArgumentException)
        {
            _proposal = null;
            _edit.Visible = true;
            _review.Visible = false;
            _message.Text = ex.Message + " Keep the existing state and any configuration backup. Correct the input and review again.";
        }
        finally { _busy = false; _next.Enabled = _proposal is null; _save.Enabled = _back.Enabled = _proposal is not null; }
    }

    private static void ValidateEnvironment(MedalSyncConfig config, bool fresh)
    {
        var install = MedalEnvironment.Locate();
        if (new MedalProcess(install).IsRunning()) throw new UnsafeOperationException("Quit Medal normally before preparing configuration.");
        if (install.AccountId != config.AccountId) throw new IncompatibleLibraryException("The account ID does not match the signed-in Medal account. Run 'medal-sync inspect' and use that account ID.");
        MedalCompatibility.Check(install).RequireCompatible();
        if (!File.Exists(config.ExpandedRclonePath)) throw new UnsafeOperationException("The rclone executable is missing. Select the bundled rclone.exe from the installed bin directory.");
        foreach (string folder in new[] { config.Remote, config.CoordinationRoot, config.CacheRoot }.Concat(config.Replication?.LocalCopyRoot is { } copy ? [copy] : Array.Empty<string>()))
            if (!Directory.Exists(folder)) throw new UnsafeOperationException($"Storage folder is unavailable: '{folder}'. Create the dedicated folder or reconnect the share, then review again.");
        string drive = Path.GetPathRoot(config.CacheRoot)!;
        if (new DriveInfo(drive).AvailableFreeSpace < ((long)config.Mount.MinFreeGiB << 30))
            throw new UnsafeOperationException("The cache drive is below the configured free-space reserve. Free space or select another SSD before saving.");
        if (config.Replication?.Targets.Count > 0 && !File.Exists(Environment.ExpandEnvironmentVariables(config.Replication.RcloneConfigPath!)))
            throw new UnsafeOperationException("The provider configuration file is missing. Authorize providers separately with 'rclone config'; keep credentials outside media and coordination folders.");
        if (fresh)
        {
            if (Directory.Exists(config.LibraryCoordinationDirectory)) throw new UnsafeOperationException("That library directory already exists. Join its library ID; never initialize it again.");
        }
        else RequireReady(config);
    }

    private static IDisposable AcquireGuard()
    {
        var instance = SessionCommands.SingleInstance();
        try { SetupMaintenance.RequireNotRunning(); return instance; }
        catch { instance.Dispose(); throw; }
    }

    private static void RequireReady(MedalSyncConfig config)
    {
        var store = CoordinationStore.Open(config.LibraryCoordinationDirectory, config.LibraryId, config.AccountId);
        var journal = store.ReadJournal();
        _ = store.Generations.ReadManifest(journal.CommittedGeneration);
        if (journal.State != OwnershipState.Clean || journal.Owner is not null || journal.SessionId is not null)
            throw new UnsafeOperationException("The existing library is not Ready. Finish its owned session before saving configuration.");
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x11) { _ending = true; message.Result = (IntPtr)1; return; }
        base.WndProc(ref message);
    }
}
