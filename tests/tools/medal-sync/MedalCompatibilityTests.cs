using System.Runtime.Versioning;
using System.Text.Json;
using MedalSync.Cli;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

public sealed class MedalCompatibilityTests : IDisposable
{
    private readonly MedalFixture _fixture = new();
    private static readonly MedalVersions Reviewed = new("2644.591.1", "2643.3040.1");
    public void Dispose() => _fixture.Dispose();

    private MedalInstall Install()
    {
        string database = _fixture.CreateLibrary("pc");
        string directory = _fixture.PathFor("install");
        Directory.CreateDirectory(Path.Combine(directory, "current"));
        string executable = Path.Combine(directory, "current", "Medal.exe");
        File.WriteAllText(executable, "synthetic; never executed");
        SetVersions(directory, Reviewed);
        return new MedalInstall(directory, database, MedalFixture.AccountId, executable, Reviewed);
    }

    private static void SetVersions(string directory, MedalVersions versions)
    {
        File.WriteAllText(Path.Combine(directory, "current", "sq.version"),
            $"<package xmlns='urn:synthetic'><metadata><version> {versions.App} </version></metadata></package>");
        File.WriteAllText(Path.Combine(directory, "version.json"), JsonSerializer.Serialize(new { RecorderHandler2Version = versions.Recorder }));
    }

    [Theory]
    [InlineData("<invalid", true)]
    [InlineData("", true)]
    [InlineData("not json", false)]
    [InlineData("[]", false)]
    [InlineData("null", false)]
    [InlineData("{\"RecorderHandler2Version\":42}", false)]
    public void BrokenVersionFilesDoNotHideTheOtherVersion(string contents, bool app)
    {
        var install = Install();
        File.WriteAllText(app ? Path.Combine(install.InstallDirectory, "current", "sq.version")
            : Path.Combine(install.InstallDirectory, "version.json"), contents);
        var versions = MedalEnvironment.ReadVersions(install.InstallDirectory);
        Assert.Equal(app ? null : Reviewed.App, versions.App);
        Assert.Equal(app ? Reviewed.Recorder : null, versions.Recorder);
        Assert.False(MedalCompatibility.Check(install).Compatible);
    }

    [Fact]
    public void MissingAppManifestStillReportsTheRecorder()
    {
        var install = Install();
        File.Delete(Path.Combine(install.InstallDirectory, "current", "sq.version"));
        Assert.Equal(new MedalVersions(null, Reviewed.Recorder), MedalEnvironment.ReadVersions(install.InstallDirectory));
    }

    [Fact]
    public void CheckUsesFreshVersionsAndLeavesLibraryAndSettingsUntouched()
    {
        var install = Install();
        MedalFixture.AddContent(install.DatabasePath, "private-clip-id", metadata: "{\"title\":\"private clip title\"}");
        string databaseBefore = Hashing.Sha256File(install.DatabasePath);
        var report = MedalCompatibility.Check(install);
        Assert.True(report.Compatible);
        Assert.Equal(4, report.SchemaVersion);
        Assert.Equal(3, report.DefinitionsVersion);
        Assert.Contains(report.SchemaFingerprint!, MedalSchema.SupportedFingerprints);
        Assert.Null(new MedalCompatibilityGuard(install).CheckForUpdate());
        SetVersions(install.InstallDirectory, Reviewed with { App = "9999.1.1" });
        report = MedalCompatibility.Check(install);
        Assert.False(report.Compatible);
        Assert.True(report.SchemaSupported);
        Assert.Equal("9999.1.1", report.Versions.App);
        Assert.Contains("9999.1.1", report.Summary, StringComparison.Ordinal);
        Assert.Contains("native database backup", report.Summary, StringComparison.Ordinal);
        Assert.Equal(databaseBefore, Hashing.Sha256File(install.DatabasePath));
        string json = JsonSerializer.Serialize(report, Json.Options);
        Assert.DoesNotContain("private-clip", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private clip title", json, StringComparison.Ordinal);
        Assert.DoesNotContain("microphone", json, StringComparison.Ordinal);
        Assert.DoesNotContain(MedalFixture.AccountId, json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ALTER TABLE contents ADD COLUMN future TEXT")]
    [InlineData("ALTER TABLE content_counts RENAME COLUMN count TO future_count")]
    [InlineData("ALTER TABLE content_counts RENAME COLUMN key TO future_key")]
    [InlineData("DROP TABLE content_counts")]
    [InlineData("UPDATE content_counts SET count = 999 WHERE key = '__defsVersion'")]
    [InlineData("PRAGMA user_version=5")]
    public void SchemaChangesAreReportedAndBlockWithoutAWrite(string change)
    {
        var install = Install();
        using (var db = MedalFixture.Open(install.DatabasePath)) MedalFixture.Execute(db, change);
        string before = Hashing.Sha256File(install.DatabasePath);
        var report = MedalCompatibility.Check(install);
        Assert.False(report.Compatible);
        Assert.False(report.SchemaSupported);
        Assert.NotNull(report.SchemaFingerprint);
        Assert.Contains("Unsupported library schema", report.Summary, StringComparison.Ordinal);
        Assert.Throws<IncompatibleLibraryException>(report.RequireCompatible);
        Assert.Equal(before, Hashing.Sha256File(install.DatabasePath));
    }

    [Fact]
    public void AnUnreadableDatabaseAndMissingExecutableAreSeparateBlockers()
    {
        var install = Install();
        File.WriteAllText(install.DatabasePath, "synthetic corrupt database");
        File.Delete(install.Executable);
        var report = MedalCompatibility.Check(install);
        Assert.False(report.Compatible);
        Assert.False(report.ExecutablePresent);
        Assert.Null(report.SchemaFingerprint);
        Assert.Equal(2, report.Blockers.Count);
    }

    [Fact]
    public void ReviewedUpdateRequiresFreshLaunchButCanPublishWithNewVersions()
    {
        var install = Install();
        var guard = new MedalCompatibilityGuard(install);
        guard.RequireLaunch();
        var updated = Reviewed with { App = "2643.583.1" };
        SetVersions(install.InstallDirectory, updated);
        Assert.Contains("Retry through Medal Sync", Assert.Throws<IncompatibleLibraryException>(guard.RequireLaunch).Message, StringComparison.Ordinal);
        Assert.Equal(updated, guard.RequirePublish());
        Assert.Contains("Medal update detected", guard.CheckForUpdate()!, StringComparison.Ordinal);
        SetVersions(install.InstallDirectory, updated with { Recorder = "future" });
        Assert.Throws<IncompatibleLibraryException>(() => guard.RequirePublish());
        Assert.Contains("handoff is blocked", guard.CheckForUpdate()!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2643.583.1", "2644.591.1", "2643.3040.1", true)]
    [InlineData("2644.591.1", "2643.583.1", "2643.3040.1", true)]
    [InlineData("2644.591.1", "2644.591.1", "2643.3040.2", false)]
    [InlineData("9999.1.1", "2644.591.1", "2643.3040.1", false)]
    [InlineData("2644.591.1", "9999.1.1", "2643.3040.1", false)]
    public void EnrollmentAllowsReviewedBuildDifferencesOnly(string shared, string local, string recorder, bool supported)
    {
        var first = new MedalVersions(shared, "2643.3040.1");
        var second = new MedalVersions(local, recorder);
        if (supported) NativeBackupBuilds.RequireCompatible(first, second);
        else Assert.Throws<IncompatibleLibraryException>(() => NativeBackupBuilds.RequireCompatible(first, second));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void CompatibilityCommandWritesStructuredResultAndFailureExitCodeWithoutConfig()
    {
        if (!OperatingSystem.IsWindows()) return;
        var install = Install();
        var output = new StringWriter();
        Assert.Equal(0, SessionCommands.WriteCompatibility(install, true, output));
        using var document = JsonDocument.Parse(output.ToString());
        Assert.True(document.RootElement.GetProperty("compatible").GetBoolean());
        SetVersions(install.InstallDirectory, Reviewed with { App = "future" });
        output = new StringWriter();
        Assert.Equal(1, SessionCommands.WriteCompatibility(install, false, output));
        Assert.Contains("future", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Keep unfinished sessions", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void InventoryCallsOutBuildIncompatibilityEvenWithASupportedSchema()
    {
        var install = Install();
        SetVersions(install.InstallDirectory, Reviewed with { App = "future" });
        var compatibility = MedalCompatibility.Check(install);
        var report = new InventoryReport(DateTimeOffset.UnixEpoch, "Synthetic PC", "synthetic", "Windows",
            "future", Reviewed.Recorder, false, null, "synthetic", null, [], @"M:\", true, null,
            new Dictionary<string, string?>(), [], [], new Dictionary<string, string>(), new Dictionary<string, string>(),
            Compatibility: compatibility);
        string text = InventoryText.Render(report);
        Assert.Contains("Compatibility: Medal app future", text, StringComparison.Ordinal);
        Assert.Contains("native database backup behavior", text[(text.IndexOf("Blockers before Gate B:", StringComparison.Ordinal))..], StringComparison.Ordinal);
    }
}
