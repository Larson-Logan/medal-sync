using System.Reflection;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Launch;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

internal static class LibrarySetupTests
{
    internal static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "medal-sync-desktop-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var savedState = new LocalState(Path.Combine(root, "save-new"));
            using (var saving = Wizard(savedState, _ => throw new InvalidOperationException("Saving configuration must never run a library command.")))
            {
                saving.Show();
                Field<ComboBox>(saving, "_mode").SelectedIndex = 1;
                var fields = Field<Dictionary<string, TextBox>>(saving, "_fields");
                fields["account"].Text = "12345";
                fields["remote"].Text = Path.Combine(root, "save-media");
                fields["cache"].Text = Path.Combine(root, "save-cache");
                fields["deviceLabel"].Text = "Desk PC";
                Field<Button>(saving, "_next").PerformClick();
                Until(() => !Field<bool>(saving, "_busy"));
                Field<Button>(saving, "_save").PerformClick();
                Until(() => !Field<bool>(saving, "_busy"));
                var saved = MedalSyncConfig.Load(savedState.ConfigPath);
                check(saved.DeviceLabel == "Desk PC" && LibrarySetupPending.Read(savedState, saved) is { NewLibrary: true }, "Saving a new configuration keeps the PC label and offers library creation in the same window");
                check(saving.Visible && !Field<Button>(saving, "_save").Enabled && Field<TextBox>(saving, "_review").Text.Contains("Quit Medal"), "Saving waits for library preview instead of applying automatically");
                saving.Close();
            }
            foreach (bool fresh in new[] { true, false })
            {
                var state = new LocalState(Path.Combine(root, fresh ? "new" : "join"));
                Directory.CreateDirectory(state.Root);
                var config = new MedalSyncConfig(1, new string('a', 32), "12345", Path.Combine(root, "coordination"), Path.Combine(root, "media"),
                    @"M:\Clips", Path.Combine(root, "cache"), Path.Combine(root, "rclone.exe"), new(5, 20, 20, 300, 30, 1000, 2, 53684), 4, []) { StorageMode = StorageMode.Local };
                File.WriteAllBytes(state.ConfigPath, Json.Serialize(config));
                var pending = LibrarySetupPending.Save(state, config, fresh);
                using var fixture = new MedalFixture();
                CoordinationStore? store = null;
                if (!fresh)
                {
                    var snapshot = LibraryDatabase.ReadSnapshot(fixture.CreateLibrary("shared"), MedalFixture.AccountId);
                    store = CoordinationStore.Create(config.LibraryCoordinationDirectory, config.LibraryId, snapshot, new AssetManifest(1, [], []),
                        new DeviceIdentity(new string('b', 32), "First PC"), new string('c', 32), new("2643.583.1", "2643.3040.1"), DateTimeOffset.UtcNow);
                }
                int applies = 0, previews = 0;
                bool medalRunning = false, conflicts = false;
                string? reviewDirectory = null;
                (int, string) Command(string[] args)
                {
                    check(args[0] == (fresh ? "init" : "enroll"), "Saved setup intent selects the existing command");
                    if (medalRunning) return (1, "Quit Medal from its tray first.");
                    bool apply = args.Contains("--apply");
                    if (!fresh)
                    {
                        string directory = args[2];
                        check(Path.GetFullPath(directory).StartsWith(state.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Enrollment review stays in private scratch state");
                        if (apply) check(directory == reviewDirectory, "Apply uses the exact preview's review directory");
                        else
                        {
                            reviewDirectory = directory;
                            Directory.CreateDirectory(directory);
                            File.WriteAllText(Path.Combine(directory, "review.json"), "synthetic review");
                        }
                    }
                    if (apply) { applies++; return (0, "Synthetic setup completed."); }
                    previews++;
                    return conflicts ? (1, "1 conflict needs manual review") : (0, "Synthetic library preview: 0 clips, 0 conflicts.");
                }
                using (var declined = Wizard(state, Command))
                {
                    declined.Show();
                    check(!Field<Panel>(declined, "_edit").Visible && !Field<Button>(declined, "_save").Enabled, "Pending setup reopens its library step and cannot apply before preview");
                    Field<Button>(declined, "_next").PerformClick();
                    Until(() => !Field<bool>(declined, "_busy"));
                    check(Field<Button>(declined, "_save").Enabled, "Successful preview enables explicit confirmation");
                    declined.Close();
                }
                check(applies == 0 && LibrarySetupPending.Read(state, config) == pending, "Declining the preview applies nothing and preserves resumable setup");
                var status = new DesktopStatusSource(() => state, () => false).Read();
                check(status.Title == "Finish library setup" && status.Phase != SessionPhase.Ready, "Skipped setup has an actionable status without claiming Ready");
                if (store is not null)
                {
                    var device = state.Device();
                    string session = new string('d', 32);
                    store.Update(r => Ownership.Acquire(r, device, session, DateTimeOffset.UtcNow), TimeSpan.FromSeconds(2));
                    check(new DesktopStatusSource(() => state, () => false).Read().Phase == SessionPhase.RecoveryNeeded,
                        "A pending setup record never hides actual interrupted ownership");
                    store.Update(r => Ownership.AbortStart(r, device, session, "synthetic test", DateTimeOffset.UtcNow), TimeSpan.FromSeconds(2));
                }
                using var wizard = Wizard(state, Command);
                wizard.Show();
                medalRunning = true;
                Field<Button>(wizard, "_next").PerformClick();
                Until(() => !Field<bool>(wizard, "_busy"));
                check(!Field<Button>(wizard, "_save").Enabled && Field<TextBox>(wizard, "_review").Text.Contains("Quit Medal"), "Running Medal refuses preview");
                medalRunning = false;
                conflicts = true;
                Field<Button>(wizard, "_next").PerformClick();
                Until(() => !Field<bool>(wizard, "_busy"));
                check(!Field<Button>(wizard, "_save").Enabled && Field<TextBox>(wizard, "_review").Text.Contains("manual review"), "Manual review blocks confirmation and retains command output");
                conflicts = false;
                Field<Button>(wizard, "_next").PerformClick();
                Until(() => !Field<bool>(wizard, "_busy"));
                medalRunning = true; // Must be rechecked by the existing command, even after a successful preview.
                Field<Button>(wizard, "_save").PerformClick();
                Until(() => !Field<bool>(wizard, "_busy"));
                check(applies == 0 && !Field<Button>(wizard, "_save").Enabled,
                    $"Medal reopening after preview refuses apply and requires a new review (new={fresh}, applies={applies}, save={Field<Button>(wizard, "_save").Enabled}, message={Field<Label>(wizard, "_message").Text})");
                medalRunning = false;
                Field<Button>(wizard, "_next").PerformClick();
                Until(() => !Field<bool>(wizard, "_busy"));
                File.AppendAllText(state.ConfigPath, " ");
                Field<Button>(wizard, "_save").PerformClick();
                Until(() => !Field<bool>(wizard, "_busy"));
                check(applies == 0 && Field<Label>(wizard, "_message").Text.Contains("Configuration changed"), "A changed configuration invalidates approval before command execution");
                Field<Button>(wizard, "_next").PerformClick();
                Until(() => !Field<bool>(wizard, "_busy"));
                Field<Button>(wizard, "_save").PerformClick();
                Until(() => !Field<bool>(wizard, "_busy"));
                check(applies == 1 && previews >= 4 && LibrarySetupPending.Read(state, config) is null, "Confirmed setup runs once and clears only its local pending record");
                wizard.Close();
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static WindowsConfigurationWizard Wizard(LocalState state, Func<string[], (int, string)> command) =>
        new(state, (_, _) => { }, () => new Guard(), runSetupCommand: command);
    private sealed class Guard : IDisposable { public void Dispose() { } }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static void Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Synthetic library setup did not finish.");
            Application.DoEvents();
            Thread.Sleep(10);
        }
        Application.DoEvents();
    }
}
