using System.Reflection;
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
                Field<ComboBox>(wizard, "_mode").SelectedIndex = 1;
                fields["account"].Text = "12345";
                fields["remote"].Text = @"D:\SyntheticMedia";
                fields["coordination"].Text = @"D:\SyntheticCoordination";
                fields["cache"].Text = @"E:\SyntheticCache";
                var candidate = wizard.Candidate();
                check(candidate.StorageMode == StorageMode.Local && candidate.LibraryId.Length == 32, "Native wizard builds a validated new local identity");
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
            byte[] original = File.ReadAllBytes(state.ConfigPath);
            using (var wizard = new WindowsConfigurationWizard(state, (_, _) => { }, () => new Guard()))
            {
                wizard.Show();
                var fields = Field<Dictionary<string, TextBox>>(wizard, "_fields");
                check(fields["library"].ReadOnly && fields["account"].ReadOnly && !Field<ComboBox>(wizard, "_identity").Enabled, "Existing account and library identities cannot be replaced in the wizard");
                wizard.Close();
                check(File.ReadAllBytes(state.ConfigPath).SequenceEqual(original), "Cancel preserves the existing configuration byte-for-byte");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static T Field<T>(object view, string name) => (T)view.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
    private sealed class Guard : IDisposable { public void Dispose() { } }
    private static void Until(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do { Application.DoEvents(); if (ready()) return; Thread.Sleep(10); } while (DateTime.UtcNow < deadline);
        throw new TimeoutException("Native configuration review did not complete.");
    }
}
