using System.Reflection;
using MedalSync;
using MedalSync.Config;
using MedalSync.Launch;
using MedalSync.Util;

internal static class ConfigurationWizardTests
{
    internal static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "MedalSyncWizardTest-" + Guid.NewGuid().ToString("N"));
        var state = new LocalState(root);
        try
        {
            using (var wizard = new WindowsConfigurationWizard(state, (_, _) => { }, () => new Guard()))
            {
                wizard.Show();
                var fields = Field<Dictionary<string, TextBox>>(wizard, "_fields");
                check(fields["coordination"].Text.Length == 0 && !fields["cloudJournal"].Visible && fields["copy"].Visible,
                    "Server mode starts without a local coordination default and hides the Drive journal");
                Field<ComboBox>(wizard, "_mode").SelectedIndex = 1;
                check(fields["coordination"].Text == Path.Combine(root, "coordination"), "Local mode fills the untouched coordination default");
                check(!fields["copy"].Visible && !fields["cloudJournal"].Visible && fields["remote"].Visible, "Local mode hides fields it never uses");
                check(Hint(wizard, "remote").Visible && Hint(wizard, "remote").Text.Contains(@"D:\MedalLibrary", StringComparison.Ordinal)
                    && fields["remote"].AccessibleDescription == Hint(wizard, "remote").Text, "Each field explains its mode-specific value to sighted and screen-reader users");
                check(Browse(wizard, "cache") is { Visible: true } cacheBrowse && cacheBrowse.AccessibleName == "Browse for SSD cache folder"
                    && Browse(wizard, "rclone")!.Visible && Browse(wizard, "capture") is null, "Path fields offer named Browse buttons; the not-yet-mounted capture drive does not");
                check(!Hint(wizard, "account").Visible || !Hint(wizard, "account").Text.Contains("Detected", StringComparison.Ordinal),
                    "Wizards with test validation never read the real Medal profile");
                fields["copy"].Text = @"E:\HiddenCopy";
                fields["cloudJournal"].Text = "hidden-cloud-journal-id";
                fields["account"].Text = "12345";
                fields["remote"].Text = @"D:\SyntheticMedia";
                fields["coordination"].Text = @"D:\SyntheticCoordination";
                fields["cache"].Text = @"E:\SyntheticCache";
                var candidate = wizard.Candidate();
                check(candidate.StorageMode == StorageMode.Local && candidate.LibraryId.Length == 32, "Native wizard builds a validated new local identity");
                check(candidate.Replication is null && candidate.CloudJournalId is null, "Values typed into hidden fields never reach the configuration");
                fields["copy"].Text = fields["cloudJournal"].Text = "";
                check(candidate.Mount.ControlPort == 53684, "New wizard setups leave rclone's browser sign-in port free");
                check(fields.Values.All(f => f.AccessibleName?.Length > 0 && f.TabStop), "Wizard text fields support native accessibility and keyboard traversal");
                Field<Button>(wizard, "_next").PerformClick();
                Until(() => !Field<bool>(wizard, "_busy"));
                check(Field<Button>(wizard, "_save").Enabled, "Review completed: " + Field<Label>(wizard, "_message").Text);
                var review = Field<TextBox>(wizard, "_review");
                check(review.Visible && review.ReadOnly && review.Text.Contains(state.ConfigPath, StringComparison.Ordinal), "Review displays the resolved state location and is read-only");
                check(wizard.AcceptButton is null && wizard.CancelButton is Button, "Review requires explicit save while Escape cancels");
                check(!Directory.Exists(root), "Native review does not create configuration, identity or journal");
                Field<Button>(wizard, "_back").PerformClick();
                fields["cache"].Text = @"M:\InvalidCache";
                Field<Button>(wizard, "_next").PerformClick();
                Until(() => Field<Button>(wizard, "_next").Enabled);
                check(!Field<Button>(wizard, "_save").Enabled && Field<Label>(wizard, "_message").Text.Contains("cacheRoot", StringComparison.Ordinal), "Invalid paths show an actionable native refusal before save");
                wizard.Close();
                check(!Directory.Exists(root), "Cancel after an invalid review leaves state absent");
                Directory.CreateDirectory(root);
                File.WriteAllBytes(state.ConfigPath, Json.Serialize(candidate));
            }
            var fresh = new LocalState(Path.Combine(root, "fresh"));
            using (var wizard = new WindowsConfigurationWizard(fresh, (_, _) => { }, () => new Guard(), () => "60098"))
            {
                wizard.Show();
                check(Field<Dictionary<string, TextBox>>(wizard, "_fields")["account"].Text == "60098"
                    && Hint(wizard, "account").Text.Contains("Detected", StringComparison.Ordinal), "A new setup fills the signed-in Medal account ID");
            }
            string? signedIn = null;
            using (var wizard = new WindowsConfigurationWizard(fresh, (_, _) => { }, () => new Guard(),
                () => signedIn ?? throw new UnsafeOperationException("Sign in to Medal with the shared account.")))
            {
                wizard.Show();
                var account = Field<Dictionary<string, TextBox>>(wizard, "_fields")["account"];
                check(account.Text.Length == 0 && Hint(wizard, "account").Text.Contains("Sign in to Medal", StringComparison.Ordinal),
                    "A missing signed-in account explains how to detect it");
                signedIn = "777";
                var detect = Action(wizard, "account")!;
                check(detect.Visible && detect.AccessibleName!.Contains("signed-in", StringComparison.Ordinal), "Detect is offered for a new setup");
                detect.PerformClick();
                check(account.Text == "777" && Hint(wizard, "account").Text.Contains("Detected", StringComparison.Ordinal), "Detect retries after signing in to Medal");
                Field<ComboBox>(wizard, "_mode").SelectedIndex = 2;
                check(Field<Dictionary<string, TextBox>>(wizard, "_fields")["cloudJournal"].Visible && !Browse(wizard, "remote")!.Visible && Browse(wizard, "copy")!.Visible,
                    "Google Drive mode shows its journal and local copy and does not browse for a Drive remote");
            }
            check(!Directory.Exists(fresh.Root), "Opening a new setup and detecting the account write nothing");
            byte[] original = File.ReadAllBytes(state.ConfigPath);
            var saved = MedalSyncConfig.Load(state.ConfigPath);
            File.WriteAllBytes(state.ConfigPath, Json.Serialize(saved with { Mount = saved.Mount with { ControlPort = 53682 } }));
            byte[] legacyOriginal = File.ReadAllBytes(state.ConfigPath);
            using (var wizard = new WindowsConfigurationWizard(state, (_, _) => { }, () => new Guard(),
                () => throw new InvalidOperationException("An existing setup must not detect a replacement account.")))
            {
                wizard.Show();
                check(Action(wizard, "account") is null && Hint(wizard, "account").Text.Contains("existing setup", StringComparison.Ordinal),
                    "An existing setup keeps its account without detection");
                check(wizard.Candidate().Mount.ControlPort == 53682, "Editing an existing setup preserves its control port until explicitly changed while Ready");
                var fields = Field<Dictionary<string, TextBox>>(wizard, "_fields");
                check(fields["library"].ReadOnly && fields["account"].ReadOnly && !Field<ComboBox>(wizard, "_identity").Enabled, "Existing account and library identities cannot be replaced in the wizard");
                wizard.Close();
                check(File.ReadAllBytes(state.ConfigPath).SequenceEqual(legacyOriginal), "Cancel preserves the existing configuration byte-for-byte");
            }
            File.WriteAllBytes(state.ConfigPath, original);

            var existing = MedalSyncConfig.Load(state.ConfigPath) with
            {
                Replication = new(null, @"C:\Synthetic\rclone.conf",
                    [new("nas2", "sftp-home:Medal") { Required = true }, new("cloud", "cloud:Medal")]),
            };
            File.WriteAllBytes(state.ConfigPath, Json.Serialize(existing));
            using (var wizard = new WindowsConfigurationWizard(state, (_, _) => { }, () => new Guard()))
            {
                var candidate = wizard.Candidate();
                check(candidate.Replication!.Targets[0].Required && !candidate.Replication.Targets[1].Required,
                    "Editing an existing setup preserves required and optional cloud copies");
                var fields = Field<Dictionary<string, TextBox>>(wizard, "_fields");
                fields["targets"].Text = "NAS2=sftp-home:Medal new=folder\r\nrequired renamed=cloud:Medal";
                candidate = wizard.Candidate();
                check(candidate.Replication!.Targets.All(t => t.Required), "An unchanged name keeps its requirement without a prefix; a renamed target can be explicitly required");
                check(candidate.Replication.Targets[0].Remote == "sftp-home:Medal new=folder", "Remote folder spaces and equals signs survive parsing");
                var review = ConfigurationReview.Prepare(state, candidate, false, (_, _) => { });
                check(review.Text.Contains("NAS2 = sftp-home:Medal new=folder (required)", StringComparison.Ordinal)
                    && review.Text.Contains("'cloud' removed (was optional)", StringComparison.Ordinal)
                    && review.Text.Contains("'renamed' added (required)", StringComparison.Ordinal), "Review calls out renamed and removed targets and their requirements");
                fields["targets"].Text = "optional nas2=sftp-home:Medal\r\nrequired cloud=cloud:Medal\r\nnew=another:Medal";
                candidate = wizard.Candidate();
                check(!candidate.Replication!.Targets[0].Required && candidate.Replication.Targets[1].Required
                    && !candidate.Replication.Targets[2].Required, "Explicit prefixes change requirements and new targets default to optional");
                review = ConfigurationReview.Prepare(state, candidate, false, (_, _) => { });
                check(review.Text.Contains("'nas2' requirement changed: required -> optional", StringComparison.Ordinal)
                    && review.Text.Contains("'cloud' requirement changed: optional -> required", StringComparison.Ordinal), "Review calls out requirement changes in both directions");
                review.Save(true, (_, _) => { });
                check(MedalSyncConfig.Load(state.ConfigPath).Replication!.Targets.SequenceEqual(candidate.Replication.Targets), "Saving preserves reviewed target requirements");
                fields["targets"].Text = "";
                check(wizard.Candidate().Replication!.Targets.Count == 0, "All cloud targets can be explicitly removed");
            }
            foreach (double hours in new[] { 0.5, 2.5 })
            {
                File.WriteAllBytes(state.ConfigPath, Json.Serialize(existing with { MaxSessionHours = hours }));
                using var wizard = new WindowsConfigurationWizard(state, (_, _) => { }, () => new Guard());
                wizard.Show();
                check(decimal.Parse(Field<NumericUpDown>(wizard, "_hours").Text) == (decimal)hours, "Fractional hours are displayed");
                var review = ConfigurationReview.Prepare(state, wizard.Candidate(), false, (_, _) => { });
                review.Save(true, (_, _) => { });
                check(MedalSyncConfig.Load(state.ConfigPath).MaxSessionHours == hours, "Saving without edits preserves fractional hours");
                wizard.Close();
            }
            foreach (double hours in new[] { 0.001, 2.555, double.Epsilon })
            {
                var unusual = existing with { MaxSessionHours = hours, Mount = existing.Mount with { CacheMaxGiB = 100001, MinFreeGiB = int.MaxValue } };
                File.WriteAllBytes(state.ConfigPath, Json.Serialize(unusual));
                using var wizard = new WindowsConfigurationWizard(state, (_, _) => { }, () => new Guard());
                wizard.Show();
                check(Field<Label>(wizard, "_message").Text.Contains("original value is kept", StringComparison.Ordinal), "Values outside the display range show a preservation message");
                var candidate = wizard.Candidate();
                check(candidate.MaxSessionHours == hours && candidate.Mount == unusual.Mount, "Unedited values outside the display range are preserved");
                var review = ConfigurationReview.Prepare(state, candidate, false, (_, _) => { });
                review.Save(true, (_, _) => { });
                check(MedalSyncConfig.Load(state.ConfigPath).MaxSessionHours == hours
                    && MedalSyncConfig.Load(state.ConfigPath).Mount == unusual.Mount, "Saved out-of-range display values round-trip without loss");
                Field<NumericUpDown>(wizard, "_hours").Value = 1.25m;
                Field<NumericUpDown>(wizard, "_cacheLimit").Value = 30;
                Field<NumericUpDown>(wizard, "_reserve").Value = 40;
                candidate = wizard.Candidate();
                check(candidate.MaxSessionHours == 1.25 && candidate.Mount.CacheMaxGiB == 30 && candidate.Mount.MinFreeGiB == 40,
                    "Explicit numeric edits replace preserved values");
                wizard.Close();
            }
            using (var wizard = new WindowsConfigurationWizard(state, (_, _) => { }, () => new Guard()))
            {
                var fields = Field<Dictionary<string, TextBox>>(wizard, "_fields");
                Field<ComboBox>(wizard, "_mode").SelectedIndex = 2;
                fields["remote"].Text = "gdrive:MedalSync";
                fields["copy"].Text = @"E:\SyntheticPersistent";
                fields["cloudConfig"].Text = @"C:\Private\rclone.conf";
                fields["cloudJournal"].Text = "synthetic-cloud-journal-id";
                var cloud = wizard.Candidate();
                cloud.Validate();
                check(cloud.StorageMode == StorageMode.Cloud && cloud.CloudJournalId == "synthetic-cloud-journal-id",
                    "Native wizard retains Drive journal identity and configures cloud handoff without SMB");
                check(!fields["coordination"].Text.StartsWith(@"\\", StringComparison.Ordinal), "Cloud setup uses a local history cache");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static T Field<T>(object view, string name) => (T)view.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
    private static object RowFor(object view, string key)
    {
        var rows = (System.Collections.IDictionary)view.GetType().GetField("_rows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
        return rows[key]!;
    }
    private static T RowPart<T>(object view, string key, string part) => (T)RowFor(view, key).GetType().GetProperty(part)!.GetValue(RowFor(view, key))!;
    private static Label Hint(object view, string key) => RowPart<Label>(view, key, "Hint");
    private static Button? Action(object view, string key) => (Button?)RowFor(view, key).GetType().GetProperty("Action")!.GetValue(RowFor(view, key));
    private static Button? Browse(object view, string key) => Action(view, key) is { Tag: not null } button ? button : null;
    private sealed class Guard : IDisposable { public void Dispose() { } }
    private static void Until(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do { Application.DoEvents(); if (ready()) return; Thread.Sleep(10); } while (DateTime.UtcNow < deadline);
        throw new TimeoutException("Native configuration review did not complete.");
    }
}
