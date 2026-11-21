using MedalSync.Cli;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Platform;
using MedalSync.Storage;
using MedalSync.Util;

namespace MedalSync.Launch;

/// <summary>Configuration and explicitly reviewed library setup through the existing commands.</summary>
internal sealed class WindowsConfigurationWizard : Form
{
    private readonly LocalState _state;
    private readonly MedalSyncConfig? _existing;
    private readonly Action<MedalSyncConfig, bool> _validate;
    private readonly Action<MedalSyncConfig> _validateExisting;
    private readonly Func<IDisposable> _acquireGuard;
    private readonly Func<string>? _detectAccount;
    private readonly bool _readNameAdvice;
    private readonly Dictionary<string, TextBox> _fields = [];
    private readonly Dictionary<string, Row> _rows = [];
    private StorageMode _appliedMode;
    private readonly HashSet<NumericUpDown> _editedNumbers = [];
    private readonly List<string> _displayWarnings = [];
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Storage mode" };
    private readonly ComboBox _identity = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "New or existing library" };
    private readonly NumericUpDown _cacheLimit = new() { Minimum = 1, Maximum = 100000, Value = 20, AccessibleName = "Cache limit in GiB" };
    private readonly NumericUpDown _reserve = new() { Minimum = 1, Maximum = 100000, Value = 20, AccessibleName = "Minimum free space in GiB" };
    private readonly NumericUpDown _hours = new() { DecimalPlaces = 2, Increment = 0.25m, Minimum = 0.01m, Maximum = 24, Value = 4, AccessibleName = "Maximum session hours" };
    private readonly Panel _edit;
    private readonly TextBox _review = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AccessibleName = "Configuration review and next steps", Visible = false };
    private readonly Button _next = new() { Text = "&Review", AutoSize = true };
    private readonly Button _save = new() { Text = "&Confirm and save", AutoSize = true, Enabled = false };
    private readonly Button _back = new() { Text = "&Back", AutoSize = true, Enabled = false };
    private readonly Label _message = new() { AutoSize = true, MaximumSize = new Size(750, 0), AccessibleName = "Setup result" };
    private ConfigurationReview? _proposal;
    private bool _busy;
    private bool _ending;
    private LibrarySetupPending? _librarySetup;
    private readonly Func<string[], (int Code, string Text)> _runSetupCommand;
    private bool _setupReviewed;
    private string? _setupConfigDigest;
    private string? _setupReviewDirectory;
    private readonly Func<string, CloudProvider?, string?, CancellationToken, SignInResult> _signIn;
    private readonly ComboBox _provider = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Cloud provider to sign in to" };
    private readonly TextBox _signInRemote = new() { AccessibleName = "rclone remote name to create or sign in again", Width = 140 };
    private readonly Button _signInButton = new() { Text = "Sign &in", AutoSize = true, AccessibleName = "Sign in to the provider in your browser" };
    private CancellationTokenSource? _signInCancel;
    /// <summary>Remotes whose sign-in in this wizard did not pass the storage check; a configuration using one is never saved.</summary>
    private readonly HashSet<string> _failedSignIns = new(StringComparer.Ordinal);

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

    internal WindowsConfigurationWizard(LocalState state, Action<MedalSyncConfig, bool>? validate = null, Func<IDisposable>? acquireGuard = null,
        Func<string>? detectAccount = null, Func<string[], (int Code, string Text)>? runSetupCommand = null,
        Func<string, CloudProvider?, string?, CancellationToken, SignInResult>? signIn = null)
    {
        _state = state;
        _signIn = signIn ?? (validate is null ? SignInWithRclone
            : (_, _, _, _) => throw new InvalidOperationException("Tests must provide an isolated sign-in."));
        _runSetupCommand = runSetupCommand ?? (validate is null ? RunSetupCommand
            : _ => throw new InvalidOperationException("Tests must provide an isolated library setup command."));
        _validate = validate ?? ValidateEnvironment;
        _readNameAdvice = validate is null;
        _validateExisting = validate is null ? RequireReady : config => validate(config, false);
        _acquireGuard = acquireGuard ?? AcquireGuard;
        // Tests that replace validation never read the real Medal profile unless they pass a detector.
        _detectAccount = detectAccount ?? (validate is null ? MedalEnvironment.SignedInAccountId : null);
        _existing = File.Exists(state.ConfigPath) ? MedalSyncConfig.Load(state.ConfigPath) : null;
        _librarySetup = _existing is null ? null : LibrarySetupPending.Read(state, _existing);
        Text = "Medal Sync configuration";
        Icon = AppIcons.LoadApplicationIcon();
        AccessibleName = Text;
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(880, 800);
        MinimumSize = new Size(650, 520);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 4, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(750, 0), Text = $"Resolved configuration: {state.ConfigPath}\r\n"
            + (_existing is null ? "Choose a storage mode; review and save it, then review creating or joining the library. Quit Medal before library setup."
                : "Existing setup found. Account, library and device identities are preserved. Quit Medal and wait for Ready before changing it.") }, 0, 0);
        _edit = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(0, 12, 12, 12) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _mode.Items.AddRange(["Server (SMB)", "Local storage", "Google Drive (no server)"]);
        _mode.SelectedIndex = _existing?.StorageMode == StorageMode.Cloud ? 2 : _existing?.StorageMode == StorageMode.Local ? 1 : 0;
        _appliedMode = SelectedMode;
        AddRow(fields, "mode", _mode);
        _identity.Items.AddRange(["Create a new library ID", "Join an existing library ID"]);
        _identity.SelectedIndex = _existing is null ? 0 : 1;
        _identity.Enabled = _existing is null;
        AddRow(fields, "identity", _identity);
        Field(fields, "library", _existing?.LibraryId ?? "");
        _fields["library"].ReadOnly = _existing is not null;
        _fields["library"].Enabled = _identity.SelectedIndex == 1;
        _identity.SelectedIndexChanged += (_, _) => { _fields["library"].Enabled = _identity.SelectedIndex == 1; ShowLibraryHint(); };
        Button? detect = _existing is null && _detectAccount is not null
            ? new Button { Text = "De&tect", AutoSize = true, AccessibleName = "Detect the signed-in Medal account" } : null;
        Field(fields, "account", _existing?.AccountId ?? "", action: detect);
        Field(fields, "deviceLabel", _existing?.DeviceLabel ?? "");
        _fields["account"].ReadOnly = _existing is not null;
        Field(fields, "remote", _existing?.Remote ?? "", Pick.Folder);
        Field(fields, "coordination", _existing?.CoordinationRoot ?? DefaultCoordination(SelectedMode), Pick.Folder);
        Field(fields, "cloudJournal", _existing?.CloudJournalId ?? "");
        Field(fields, "capture", _existing?.CaptureFolder ?? @"M:\Clips");
        Field(fields, "cache", _existing?.CacheRoot ?? "", Pick.Folder);
        Field(fields, "rclone", _existing?.RclonePath ?? ConfigCommands.DefaultRclonePath(), Pick.Executable);
        Field(fields, "copy", _existing?.Replication?.LocalCopyRoot ?? "", Pick.Folder);
        Field(fields, "cloudConfig", _existing?.Replication?.RcloneConfigPath ?? "", Pick.Credentials);
        _provider.Items.AddRange(["Google Drive", "OneDrive", "Dropbox"]);
        _provider.SelectedIndex = 0;
        var signInRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, AccessibleName = "Cloud sign-in" };
        signInRow.Controls.AddRange([_provider, _signInRemote, _signInButton]);
        _signInButton.Click += async (_, _) => await SignIn();
        AddRow(fields, "signIn", signInRow);
        Field(fields, "targets", string.Join(Environment.NewLine, _existing?.Replication?.Targets.Select(t => (t.Required ? "required " : "optional ") + t.Name + "=" + t.Remote) ?? []), multiline: true);
        Field(fields, "syncFolder", _existing?.SyncExclusions.FirstOrDefault()?.FolderId ?? "");
        Field(fields, "syncPattern", _existing?.SyncExclusions.FirstOrDefault()?.Pattern ?? "/Videos/Clips");
        LoadNumber(_cacheLimit, _existing?.Mount.CacheMaxGiB ?? 20);
        LoadNumber(_reserve, _existing?.Mount.MinFreeGiB ?? 20);
        double hours = _existing?.MaxSessionHours ?? 4;
        LoadNumber(_hours, (decimal)hours, hours != (double)(decimal)hours);
        _message.Text = string.Join(Environment.NewLine, _displayWarnings);
        AddRow(fields, "cacheLimit", _cacheLimit);
        AddRow(fields, "reserve", _reserve);
        AddRow(fields, "hours", _hours);
        _mode.SelectedIndexChanged += (_, _) => ApplyMode();
        if (detect is not null) detect.Click += (_, _) => { if (!_busy) DetectAccount(); };
        ApplyMode();
        ShowLibraryHint();
        if (_existing is not null) SetHint("account", "Kept from the existing setup.");
        else if (_detectAccount is null) SetHint("account", "Numeric ID from 'medal-sync inspect'. It must match the account signed in to Medal.");
        else DetectAccount();
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
        _next.Click += async (_, _) => { if (_librarySetup is null) await Review(); else await ReviewLibrary(); };
        _back.Click += (_, _) => { _proposal = null; _review.Visible = false; _edit.Visible = true; _next.Enabled = true; _save.Enabled = _back.Enabled = false; AcceptButton = _next; };
        _save.Click += async (_, _) => { if (_librarySetup is null) await Save(); else await ApplyLibrary(); };
        FormClosing += (_, e) => { if (_busy && e.CloseReason == CloseReason.UserClosing) e.Cancel = true; };
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        if (_librarySetup is not null) ShowLibrarySetup();
    }

    private enum Pick { None, Folder, Executable, Credentials }

    private sealed record Row(Label Caption, Control Input, Button? Action, Label Hint);

    private StorageMode SelectedMode => _mode.SelectedIndex switch { 2 => StorageMode.Cloud, 1 => StorageMode.Local, _ => StorageMode.Server };

    /// <summary>Fields a storage mode never uses are hidden and ignored, so a hidden value cannot reach the saved file.</summary>
    internal static bool Applies(string key, StorageMode mode) => key switch
    {
        "cloudJournal" => mode == StorageMode.Cloud,
        "copy" => mode != StorageMode.Local,
        _ => true,
    };

    // A Google Drive remote is not a folder on this PC.
    private static bool CanBrowse(string key, StorageMode mode) => key != "remote" || mode != StorageMode.Cloud;

    private string DefaultCoordination(StorageMode mode) => mode == StorageMode.Server ? "" : Path.Combine(_state.Root, "coordination");

    private static (string Caption, string Hint) Describe(string key, StorageMode mode) => key switch
    {
        "mode" => ("Storage &mode", mode switch
        {
            StorageMode.Local => "Clips and the ownership journal stay on this PC.",
            StorageMode.Cloud => "Clips and library handoff go through Google Drive without an SMB server. Medal records into a persistent local copy.",
            _ => "Clips upload to a private SMB share or rclone remote. PCs hand off the library through a journal on the share.",
        }),
        "identity" => ("&Library choice", ""),
        "library" => ("&Existing library ID", ""),
        "account" => ("Medal &account ID", ""),
        "deviceLabel" => ("PC display &name", "Optional name other PCs see, such as Desk PC or Living room. Up to 64 printable characters; ownership still uses this PC's unique ID."),
        "remote" => mode switch
        {
            StorageMode.Local => ("&Storage folder", @"Dedicated folder on this PC that keeps every clip, such as D:\MedalLibrary."),
            StorageMode.Cloud => ("Drive &storage folder", "Google Drive rclone remote and dedicated folder, such as gdrive:MedalSync."),
            _ => ("&Storage share or remote", @"Private UNC share such as \\server\Medal\Media, or an rclone remote and folder such as onedrive:MedalSync."),
        },
        "coordination" => mode switch
        {
            StorageMode.Server => ("&Coordination share", @"UNC folder on the private share for the ownership journal, such as \\server\Medal\Coordination."),
            StorageMode.Cloud => ("&Coordination history cache", "Dedicated local folder for downloaded library history. Ownership is read from Google Drive."),
            _ => ("&Coordination folder", "Dedicated local folder for the ownership journal, outside the storage, cache and capture folders."),
        },
        "cloudJournal" => ("Drive &journal ID", "From 'medal-sync storage cloud-id'. Use the same ID on every PC that shares this library."),
        "capture" => ("Capture folder / &drive", @"Folder on the clip drive Medal Sync mounts at launch, such as M:\Clips. Drive D to Z, and not a drive root."),
        "cache" => ("SSD cac&he folder", "Local SSD folder for the clip drive's cache. Not the share or the clip drive."),
        "rclone" => ("&Rclone executable", "The rclone.exe installed with Medal Sync is selected by default."),
        "copy" => mode == StorageMode.Cloud
            ? ("Persistent local cop&y", "Required. Medal records into this complete local copy; Drive receives verified uploads.")
            : ("Persistent local cop&y (optional)", "Keeps the complete library on this PC and avoids uploading unreferenced replay-buffer segments. Needs space for every clip; recommended on slow links. Required for an rclone remote."),
        "cloudConfig" => mode == StorageMode.Cloud
            ? ("Provider credential &file", "rclone.conf holding the Google Drive sign-in from 'rclone config'. Required in this mode.")
            : ("Provider credential &file (optional)", "rclone.conf from 'rclone config'. Required for cloud copies or an rclone storage remote."),
        "signIn" => ("Cloud sign-&in", "Choose the provider and a remote name (such as gdrive), then Sign in: the provider's page opens in your browser. "
            + "Medal Sync saves the sign-in to the provider credential file above, or to one in its private folder, only after a storage check passes. "
            + "It never sees the token. For Google Drive, consider your own OAuth client (" + ProviderSignIn.GoogleClientGuide + "). "
            + ProviderSignIn.GoogleTestingWarning),
        "targets" => ("Cloud targets (one per line)", "Use name=remote:folder. Prefix with 'required ' to block handoff until verified, or 'optional ' to allow retry later. Existing names keep their requirement when no prefix is given."),
        "syncFolder" => ("Sync folder ID (optional)", "Only when Syncthing shares a folder that contains the clip folder. Most setups leave this empty."),
        "syncPattern" => ("Sync exclusion pattern", "Rooted folder the Syncthing folder must ignore, such as /Videos/Clips."),
        "cacheLimit" => ("Cache limit (GiB)", "Soft size limit for cached clips. New recordings stay cached until they upload."),
        "reserve" => ("Minimum free space (GiB)", "Free space to keep on the cache's disk."),
        "hours" => ("Maximum session hours", "Launch checks that this much recording fits on the cache's disk above the free-space reserve."),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown configuration field."),
    };

    private void AddRow(TableLayoutPanel table, string key, Control input, Button? action = null)
    {
        int row = table.RowCount++;
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(3, 5, 3, 5);
        var caption = new Label { AutoSize = true, Anchor = AnchorStyles.Left, UseMnemonic = true };
        table.Controls.Add(caption, 0, row);
        table.Controls.Add(input, 1, row);
        if (action is null) table.SetColumnSpan(input, 2);
        else
        {
            action.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            action.Margin = new Padding(3, 4, 3, 4);
            table.Controls.Add(action, 2, row);
        }
        var hint = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(600, 0), Margin = new Padding(3, 0, 3, 8), UseMnemonic = false };
        int hintRow = table.RowCount++;
        table.Controls.Add(hint, 1, hintRow);
        table.SetColumnSpan(hint, 2);
        _rows.Add(key, new(caption, input, action, hint));
    }

    private void Field(TableLayoutPanel table, string key, string value, Pick pick = Pick.None, bool multiline = false, Button? action = null)
    {
        var text = new TextBox { Text = value, Multiline = multiline };
        if (multiline) { text.Height = 55; text.ScrollBars = ScrollBars.Vertical; }
        _fields.Add(key, text);
        if (pick != Pick.None)
        {
            action = new Button { Text = "Browse...", AutoSize = true, Tag = pick };
            action.Click += (_, _) => { if (!_busy) Browse(key, pick); };
        }
        AddRow(table, key, text, action);
    }

    private void ApplyMode()
    {
        StorageMode mode = SelectedMode;
        // A new setup's untouched default coordination location follows the mode; saved and typed values are kept.
        if (_existing is null && _fields["coordination"].Text.Trim() == DefaultCoordination(_appliedMode))
            _fields["coordination"].Text = DefaultCoordination(mode);
        _appliedMode = mode;
        _edit.SuspendLayout();
        foreach (var (key, row) in _rows)
        {
            bool shown = Applies(key, mode);
            var (caption, hint) = Describe(key, mode);
            row.Caption.Text = caption;
            row.Caption.Visible = row.Input.Visible = shown;
            if (row.Input is TextBox) row.Input.AccessibleName = caption.Replace("&", "", StringComparison.Ordinal);
            if (row.Action is not null)
            {
                bool action = shown && CanBrowse(key, mode);
                row.Action.Visible = action;
                // Without its button the field takes the whole row.
                if (row.Input.Parent is TableLayoutPanel table) table.SetColumnSpan(row.Input, action ? 1 : 2);
                if (row.Action.Tag is Pick) row.Action.AccessibleName = "Browse for " + caption.Replace("&", "", StringComparison.Ordinal);
            }
            if (key is not ("account" or "library")) SetHint(key, hint);
            else row.Hint.Visible = shown && row.Hint.Text.Length > 0;
        }
        _edit.ResumeLayout(true);
    }

    private void SetHint(string key, string text)
    {
        var row = _rows[key];
        row.Hint.Text = text;
        row.Input.AccessibleDescription = text.Length > 0 ? text : null;
        row.Hint.Visible = Applies(key, SelectedMode) && text.Length > 0;
    }

    private void ShowLibraryHint() => SetHint("library", _existing is not null ? "Kept from the existing setup."
        : _identity.SelectedIndex == 0 ? "A new ID is generated when you review. Other PCs join this library with that ID."
        : "Copy libraryId from config.json on a PC that already uses this library.");

    /// <summary>Reads only the signed-in library's file name; Medal's database is never opened here.</summary>
    private void DetectAccount()
    {
        if (_detectAccount is null) return;
        try
        {
            _fields["account"].Text = _detectAccount();
            SetHint("account", "Detected from the Medal account signed in on this PC.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MedalSyncException)
        {
            SetHint("account", "Not detected. " + ex.Message + " Then choose Detect, or enter the ID from 'medal-sync inspect'.");
        }
    }

    private void Browse(string key, Pick pick)
    {
        var field = _fields[key];
        string title = "Select " + field.AccessibleName;
        string current = Environment.ExpandEnvironmentVariables(field.Text.Trim());
        if (pick == Pick.Folder)
        {
            using var folder = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true, ShowNewFolderButton = true };
            if (current.Length > 0 && Directory.Exists(current)) folder.InitialDirectory = current;
            if (folder.ShowDialog(this) == DialogResult.OK) field.Text = folder.SelectedPath;
            return;
        }
        using var file = new OpenFileDialog
        {
            Title = title,
            CheckFileExists = true,
            RestoreDirectory = true,
            Filter = pick == Pick.Executable ? "rclone (rclone.exe)|rclone.exe|Programs (*.exe)|*.exe" : "rclone configuration (*.conf)|*.conf|All files (*.*)|*.*",
        };
        if (current.Length > 0 && Path.GetDirectoryName(current) is { Length: > 0 } directory && Directory.Exists(directory))
        {
            file.InitialDirectory = directory;
            file.FileName = Path.GetFileName(current);
        }
        if (file.ShowDialog(this) == DialogResult.OK) field.Text = file.FileName;
    }

    private void LoadNumber(NumericUpDown control, decimal saved, bool precisionLost = false)
    {
        decimal displayed = Math.Clamp(decimal.Round(saved, control.DecimalPlaces), control.Minimum, control.Maximum);
        control.Value = displayed;
        if (displayed != saved || precisionLost)
            _displayWarnings.Add($"Saved {control.AccessibleName} cannot be displayed exactly. The original value is kept unless you edit this field; Review shows the value that will be saved.");
        control.ValueChanged += (_, _) => _editedNumbers.Add(control);
    }

    internal MedalSyncConfig Candidate()
    {
        StorageMode mode = SelectedMode;
        string Value(string key) => _fields[key].Text.Trim();
        string? Optional(string key) => Applies(key, mode) && Value(key) is { Length: > 0 } value ? value : null;
        bool fresh = _identity.SelectedIndex == 0;
        string library = fresh ? Guid.NewGuid().ToString("N") : Value("library");
        if (string.IsNullOrEmpty(library)) throw new InvalidDataException("Enter the existing library ID to join; obtain it from the configured PC.");
        if (string.IsNullOrEmpty(Value("account")) || Value("account").All(c => c == '0'))
            throw new InvalidDataException("Enter your numeric Medal account ID from 'medal-sync inspect'.");
        var targets = new List<ReplicaTarget>();
        foreach (string line in Value("targets").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string entry = line;
            bool? required = null;
            foreach (string prefix in new[] { "required ", "optional " })
            {
                if (!entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                required = prefix == "required ";
                entry = entry[prefix.Length..].TrimStart();
                break;
            }
            string[] pair = entry.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2) throw new InvalidDataException("Each cloud target must be name=remote:folder, one per line.");
            required ??= _existing?.Replication?.Targets.FirstOrDefault(t => t.Name.Equals(pair[0], StringComparison.OrdinalIgnoreCase))?.Required ?? false;
            targets.Add(new(pair[0], pair[1]) { Required = required.Value });
        }
        var replication = _existing?.Replication ?? new ReplicationConfig(null, null, []);
        replication = replication with { LocalCopyRoot = Optional("copy"), RcloneConfigPath = Optional("cloudConfig"), Targets = targets };
        var exclusions = _existing?.SyncExclusions.Skip(1).ToList() ?? [];
        if (Optional("syncFolder") is { } folder) exclusions.Insert(0, new(folder, Value("syncPattern")));
        return new(1, library, Value("account"), Value("coordination"), Value("remote"), Value("capture"), Value("cache"), Value("rclone"),
            (_existing?.Mount ?? new MountConfig(5, 20, 20, 3600, 30, 1000, 2, MountConfig.DefaultControlPort)) with
            {
                CacheMaxGiB = _existing is not null && !_editedNumbers.Contains(_cacheLimit) ? _existing.Mount.CacheMaxGiB : (int)_cacheLimit.Value,
                MinFreeGiB = _existing is not null && !_editedNumbers.Contains(_reserve) ? _existing.Mount.MinFreeGiB : (int)_reserve.Value,
            },
            _existing is not null && !_editedNumbers.Contains(_hours) ? _existing.MaxSessionHours : (double)_hours.Value, exclusions)
        {
            StorageMode = mode,
            CloudJournalId = Optional("cloudJournal"),
            BetaUpdates = _existing?.BetaUpdates ?? false,
            DeviceLabel = Optional("deviceLabel"),
            Replication = replication.LocalCopyRoot is null && replication.RcloneConfigPath is null && targets.Count == 0 ? null : replication,
        };
    }

    /// <summary>
    /// Creates or reconnects the named remote in the provider credential file (or Medal Sync's private one), then checks
    /// it. Choosing Sign in again while it runs cancels it; the credential file changes only after a passing check.
    /// </summary>
    private async Task SignIn()
    {
        if (_signInCancel is { } running) { running.Cancel(); return; }
        if (_busy || _ending) return;
        string remote = _signInRemote.Text.Trim();
        if (remote.Length == 0) remote = ProviderSignIn.RemoteOf(_fields["remote"].Text.Trim()) ?? "";
        _signInRemote.Text = remote;
        var provider = (CloudProvider)_provider.SelectedIndex;
        string? configured = _fields["cloudConfig"].Text.Trim() is { Length: > 0 } path ? path : null;
        using var cancel = new CancellationTokenSource();
        _signInCancel = cancel;
        _busy = true;
        _next.Enabled = false;
        _signInButton.Text = "Cancel sign-&in";
        SetHint("signIn", "Waiting for you to finish signing in in your browser. Choose Cancel sign-in to stop; nothing is saved until the check passes.");
        try
        {
            var result = await Task.Run(() => _signIn(remote, provider, configured, cancel.Token));
            if (_ending) return;
            if (result.Succeeded)
            {
                _failedSignIns.Remove(remote);
                if (configured is null && result.ConfigPath is { } saved) _fields["cloudConfig"].Text = saved;
            }
            else if (ProviderSignIn.ValidRemoteName(remote)) _failedSignIns.Add(remote);
            SetHint("signIn", result.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or MedalSyncException)
        {
            if (ProviderSignIn.ValidRemoteName(remote)) _failedSignIns.Add(remote);
            SetHint("signIn", ex.Message);
        }
        finally
        {
            _signInCancel = null;
            _busy = false;
            _signInButton.Text = "Sign &in";
            if (!_ending) _next.Enabled = _proposal is null;
        }
    }

    private SignInResult SignInWithRclone(string remote, CloudProvider? provider, string? configPath, CancellationToken cancel)
    {
        // Runs on one worker thread from start to end, which the instance lock (a mutex) requires.
        using var instance = SignInCommands.HoldNoSession(_existing);
        string rclone = Environment.ExpandEnvironmentVariables(_fields["rclone"].Text.Trim());
        if (!File.Exists(rclone)) throw new UnsafeOperationException("The rclone executable is missing. Select the bundled rclone.exe before signing in.");
        return new ProviderSignIn(rclone, _state).SignIn(remote, provider, configPath, cancel);
    }

    /// <summary>A configuration that uses a remote whose sign-in failed here is never saved.</summary>
    private void RequireSignedIn(MedalSyncConfig candidate)
    {
        var used = new[] { candidate.Remote }.Concat(candidate.Replication?.Targets.Select(t => t.Remote) ?? [])
            .Select(ProviderSignIn.RemoteOf).Where(r => r is not null && _failedSignIns.Contains(r)).ToList();
        if (used.Count > 0)
            throw new InvalidDataException($"Sign-in to '{used[0]}' did not pass the storage check. Sign in again, or choose another remote, before saving.");
    }

    private async Task Review()
    {
        if (_busy || _ending) return;
        await Work(async () =>
        {
            var candidate = Candidate();
            RequireSignedIn(candidate);
            bool fresh = _identity.SelectedIndex == 0;
            _proposal = await Task.Run(() => { using var guard = _acquireGuard(); return ConfigurationReview.Prepare(_state, candidate, fresh, _validate, _validateExisting); });
            if (_ending) return;
            _review.Text = _proposal.Text;
            if (_readNameAdvice && !fresh)
            {
                try
                {
                    string? advice = await Task.Run(() => DeviceNameAdvice.Read(candidate, _state, CoordinationStore.Open(candidate)));
                    if (advice is not null) _review.AppendText("\r\n\r\n" + advice);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or MedalSyncException) { /* Advisory only. */ }
            }
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
        bool fresh = _identity.SelectedIndex == 0;
        await Work(async () =>
        {
            RequireSignedIn(_proposal.Config);
            string? backup = await Task.Run(() =>
            {
                using var guard = _acquireGuard();
                string? saved = _proposal.Save(true, _validate, _validateExisting);
                if (!_proposal.ReplacesExisting)
                    _librarySetup = LibrarySetupPending.Save(_state, _proposal.Config, fresh);
                return saved;
            });
            if (_ending) return;
            if (!_proposal.ReplacesExisting)
            {
                ShowLibrarySetup();
                return;
            }
            MessageBox.Show(this, "Configuration saved and verified.\r\n" + (backup is null ? "" : $"Previous configuration: {backup}\r\n")
                + "Launch Medal through Medal Sync when the library is Ready.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        finally
        {
            _busy = false;
            if (_librarySetup is not null) ShowLibrarySetup();
            else { _next.Enabled = _proposal is null; _save.Enabled = _back.Enabled = _proposal is not null; }
        }
    }

    private void ShowLibrarySetup()
    {
        _edit.Visible = false;
        _review.Visible = true;
        _review.BringToFront();
        _back.Enabled = false;
        _next.Text = "&Review library";
        _next.Enabled = !_busy;
        _save.Text = _librarySetup!.NewLibrary ? "&Confirm and create library" : "&Confirm and join library";
        _save.Enabled = !_busy && _setupReviewed;
        AcceptButton = null;
        if (!_setupReviewed)
            _review.Text = $"Configuration saved at {_state.ConfigPath}.\r\nQuit Medal normally before continuing.\r\n"
                + $"Review the existing command's preview to {_librarySetup.Action} this library. Confirm only after reading it.\r\n"
                + "You can close this window and return through Configure. The setup guide and command-line operations remain available.";
        _message.Text = "Library setup still needs review and explicit confirmation. Closing this window skips it for now.";
    }

    private static (int Code, string Text) RunSetupCommand(string[] arguments)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int code = CommandRunner.Run(arguments, output, error);
        return (code, output + Environment.NewLine + error);
    }

    private string[] SetupArguments(bool apply) => _librarySetup!.NewLibrary
        ? apply ? ["init", "--apply"] : ["init"]
        : apply ? ["enroll", "--out", _setupReviewDirectory!, "--apply"] : ["enroll", "--out", _setupReviewDirectory!];

    private void RequireSetupIntent()
    {
        var config = MedalSyncConfig.Load(_state.ConfigPath);
        if (LibrarySetupPending.Read(_state, config) != _librarySetup)
            throw new UnsafeOperationException("Pending setup changed. Reopen Configure before reviewing this library.");
    }

    private async Task ReviewLibrary()
    {
        if (_busy || _ending || _librarySetup is null) return;
        _setupReviewed = false;
        _setupConfigDigest = null;
        _setupReviewDirectory = Path.Combine(_state.Root, "enrollment-reviews", Guid.NewGuid().ToString("N"));
        await LibraryWork(async () =>
        {
            RequireSetupIntent();
            string digest = Hashing.Sha256File(_state.ConfigPath);
            var result = await Task.Run(() => _runSetupCommand(SetupArguments(false)));
            if (_ending) return;
            _review.Text = result.Text + (!_librarySetup.NewLibrary && Directory.Exists(_setupReviewDirectory)
                ? $"\r\nPrivate review folder: {_setupReviewDirectory}" : "");
            _setupReviewed = result.Code == 0 && Hashing.Sha256File(_state.ConfigPath) == digest;
            _setupConfigDigest = _setupReviewed ? digest : null;
            _message.Text = _setupReviewed ? "Read the entire preview. Confirm to apply, or close to skip. Medal must stay quit."
                : "Setup was refused or needs manual review. Keep the review files, resolve the reported problem, then review again.";
        });
    }

    private async Task ApplyLibrary()
    {
        if (_busy || _ending || _librarySetup is null || !_setupReviewed) return;
        _setupReviewed = false; // Every retry needs a new preview, including after partial enrollment.
        await LibraryWork(async () =>
        {
            RequireSetupIntent();
            if (_setupConfigDigest != Hashing.Sha256File(_state.ConfigPath))
                throw new UnsafeOperationException("Configuration changed after the preview. Reopen Configure and review again.");
            var result = await Task.Run(() => _runSetupCommand(SetupArguments(true)));
            if (_ending) return;
            _review.AppendText("\r\n\r\n" + result.Text);
            if (result.Code != 0)
            {
                _message.Text = "Setup did not complete. Keep the review files. If enrollment was interrupted, use enroll recover --out with the review folder shown above.";
                return;
            }
            var config = MedalSyncConfig.Load(_state.ConfigPath);
            if (LibrarySetupPending.Read(_state, config) is not null) _librarySetup.Complete(_state, config);
            _librarySetup = null;
            _next.Enabled = _save.Enabled = _back.Enabled = false;
            _message.Text = "Library setup completed. You can close this window and launch Medal through Medal Sync.";
        });
    }

    private async Task LibraryWork(Func<Task> work)
    {
        _busy = true;
        _next.Enabled = _save.Enabled = _back.Enabled = false;
        _message.Text = "Checking the library with Medal Sync's existing setup command. Keep Medal quit.";
        try { await work(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or MedalSyncException or ArgumentException)
        {
            _setupReviewed = false;
            _message.Text = ex.Message + " Keep setup and review files; correct the problem before reviewing again.";
        }
        finally
        {
            _busy = false;
            _next.Enabled = _librarySetup is not null;
            _save.Enabled = _librarySetup is not null && _setupReviewed;
        }
    }

    private static void ValidateEnvironment(MedalSyncConfig config, bool fresh)
    {
        var install = MedalEnvironment.Locate();
        if (new MedalProcess(install).IsRunning()) throw new UnsafeOperationException("Quit Medal normally before preparing configuration.");
        if (install.AccountId != config.AccountId) throw new IncompatibleLibraryException("The account ID does not match the signed-in Medal account. Run 'medal-sync inspect' and use that account ID.");
        MedalCompatibility.Check(install).RequireCompatible();
        if (!File.Exists(config.ExpandedRclonePath)) throw new UnsafeOperationException("The rclone executable is missing. Select the bundled rclone.exe from the installed bin directory.");
        // Folders, disk type and free space are measured on the volumes that really hold them.
        if (StorageReadiness.Problems(config, Directory.Exists, StorageVolume.Describe) is { Count: > 0 } problems)
            throw new UnsafeOperationException(string.Join(" ", problems));
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
        var store = CoordinationStore.Open(config);
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
