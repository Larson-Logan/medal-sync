using System.Text.Json.Nodes;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Tests.Fixtures;

namespace MedalSync.Tests;

/// <summary>The support summary is allowlisted and tokenized; synthetic canaries prove private values cannot leak.</summary>
public sealed class DiagnosticSummaryTests : IDisposable
{
    private const string LibraryId = "c0ffee00c0ffee00c0ffee00c0ffee00";
    private const string AccountId = "987654321";
    private const string ThisDevice = "dddddddddddddddddddddddddddddddd";
    private const string OtherDevice = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string Session = "ffffffffffffffffffffffffffffffff";
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 20, 15, 42, TimeSpan.Zero);

    /// <summary>Every private value a real PC could expose, each easy to find in output.</summary>
    private static readonly string[] Canaries =
    [
        LibraryId, AccountId, ThisDevice, OtherDevice, Session,
        "CanaryHost", "canaryuser", "CanaryDomain", "CanaryShare", "CanaryCoordination", "CanaryMedia", "CanaryCache", "CanaryTools",
        "CanaryLocalCopy", "CanarySecrets", "canaryremote", "CanaryCloudFolder", "canary-target", "canary-vault", "canarysync",
        "CanaryOtherPc", "CanaryReason", "CanaryClipTitle", "CanaryToken", "CanaryProfile",
    ];

    private readonly MedalFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static MedalSyncConfig Config() => new(1, LibraryId, AccountId,
        @"\\CanaryShare\CanaryCoordination", "canaryremote:CanaryCloudFolder", @"M:\Clips", @"E:\CanaryCache", @"C:\CanaryTools\rclone.exe",
        new(5, 20, 20, 300, 30, 1000, 2, 53682) { BandwidthLimitMiB = 5 }, 4, [new SyncExclusion("canarysync", "/Clips/CanaryMedia")])
    {
        Replication = new(@"E:\CanaryLocalCopy", @"C:\CanarySecrets\rclone.conf",
            [new ReplicaTarget("canary-target", "canaryremote:CanaryCloudFolder/one"), new ReplicaTarget("canary-vault", "canaryremote:CanaryCloudFolder/two") { Required = true }]),
    };

    private static Exception Leaky(string type = "io") =>
        type == "io" ? new IOException(string.Join(" ", Canaries) + @" at \\CanaryShare\CanaryMedia\CanaryClipTitle.mp4 token=CanaryToken")
            : new InvalidDataException("'D:\\Profiles\\canaryuser\\CanaryProfile\\config.json': " + string.Join(" ", Canaries));

    private static DiagnosticFacts Full() => new()
    {
        Now = Now,
        ToolVersion = "1.1.0",
        WindowsVersion = "10.0.26100",
        SetupInstall = true,
        MedalRunning = false,
        SignedInAccount = AccountId,
        BitrateMbps = 50.4,
        Compatibility = new MedalCompatibilityReport(new("2644.591.1", "2643.3040.1"), true, 4, 3, new string('a', 64), true, []),
        RclonePresent = true,
        RcloneSha256 = new string('B', 64),
        WinFspInstalled = true,
        ConfigPresent = true,
        Config = Config(),
        CacheFreeBytes = 120_000_000_000,
        CacheTotalBytes = 500_000_000_000,
        SessionFits = true,
        CoordinationReachable = true,
        Device = new(ThisDevice, "CanaryHost (canaryuser)"),
        Journal = new(1, LibraryId, AccountId, OwnershipState.RecoveryRequired, new(OtherDevice, "CanaryOtherPc"), Session, 41, 7,
            Now.AddHours(-3), "CanaryReason: CanaryClipTitle"),
        PendingSwitchRequests = 1,
        Phase = SessionPhase.RecoveryNeeded,
        PendingCloudCopies = ["canary-target"],
        LastVerifiedAt = Now.AddDays(-2),
        Private = ["CanaryHost", "canaryuser", "CanaryDomain", @"D:\Profiles\canaryuser\CanaryProfile"],
    };

    private static JsonObject Parse(string report) => JsonNode.Parse(report)!.AsObject();

    private static void AssertNoCanary(string report)
    {
        foreach (string canary in Canaries) Assert.DoesNotContain(canary, report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@":\", report, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\\\\", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AFullReportKeepsUsefulFactsAndNoPrivateValue()
    {
        string text = DiagnosticSummary.Build(Full());
        var report = Parse(text);

        AssertNoCanary(text);
        Assert.Equal(1, (int)report["format"]!);
        Assert.Equal("medal-sync-diagnostic-summary", (string)report["kind"]!);
        Assert.Equal("2026-10-29T20:15Z", (string)report["generatedAt"]!);
        Assert.Equal("1.1.0", (string)report["medalSync"]!["version"]!);
        Assert.Equal("setup", (string)report["medalSync"]!["installation"]!);
        Assert.Equal("2644.591.1", (string)report["medal"]!["app"]!);
        Assert.True((bool)report["medal"]!["schemaSupported"]!);
        Assert.Equal(new string('b', 64), (string)report["dependencies"]!["rcloneSha256"]!);

        var config = report["configuration"]!;
        Assert.Equal("library-1", (string)config["library"]!);
        Assert.True((bool)config["accountMatchesMedal"]!);
        Assert.Equal("SERVER", (string)config["storageMode"]!);
        Assert.Equal("rclone-remote", (string)config["primary"]!);
        Assert.Equal(["target-1", "target-2"], config["cloudTargets"]!.AsArray().Select(t => (string)t!["target"]!));
        Assert.True((bool)config["cloudTargets"]![1]!["required"]!);
        Assert.True((bool)config["uploadLimitedWhileRecording"]!);

        Assert.Equal("50-200 GB", (string)report["capacity"]!["cacheFree"]!);
        Assert.Equal(50, (double)report["capacity"]!["bitrateMbps"]!);

        var ownership = report["ownership"]!;
        Assert.Equal("RECOVERY_REQUIRED", (string)ownership["state"]!);
        Assert.Equal("pc-1", (string)ownership["thisPc"]!);
        Assert.Equal("pc-2", (string)ownership["owner"]!);
        Assert.Equal(41, (long)ownership["committedGeneration"]!);
        Assert.Equal("under 1 day", (string)ownership["updatedAgo"]!);
        Assert.Equal(["target-1"], report["replication"]!["pendingCloudCopies"]!.AsArray().Select(t => (string)t!));
        Assert.Equal("under 7 days", (string)report["replication"]!["lastVerifiedAgo"]!);
        Assert.Equal(["cloud-copy-pending", "recovery-required"], report["refusals"]!.AsArray().Select(r => (string)r!));
    }

    [Fact]
    public void ErrorsAreReportedByKindNeverByMessage()
    {
        string text = DiagnosticSummary.Build(Full() with
        {
            InstallError = Leaky("data"),
            MedalError = Leaky(),
            Compatibility = null,
            DependencyError = Leaky(),
            Config = null,
            ConfigError = Leaky("data"),
            CapacityError = Leaky(),
            Journal = null,
            JournalError = Leaky(),
        });
        var report = Parse(text);

        AssertNoCanary(text);
        Assert.Equal("unavailable: InvalidDataException", (string)report["medalSync"]!["installation"]!);
        Assert.Equal("unavailable: IOException", (string)report["medal"]!["status"]!);
        Assert.Equal("unavailable: InvalidDataException", (string)report["configuration"]!["status"]!);
        Assert.Equal("not-configured", (string)report["replication"]!["status"]!);
        Assert.Equal("unavailable: IOException", (string)report["capacity"]!["status"]!);
        Assert.Contains("configuration-invalid", report["refusals"]!.AsArray().Select(r => (string)r!));
    }

    [Fact]
    public void MissingConfigurationIsStillUseful()
    {
        var report = Parse(DiagnosticSummary.Build(Full() with { ConfigPresent = false, Config = null, Journal = null }));

        Assert.Equal("missing", (string)report["configuration"]!["status"]!);
        Assert.Equal("not-configured", (string)report["ownership"]!["status"]!);
        Assert.Equal("2644.591.1", (string)report["medal"]!["app"]!);
        Assert.Contains("not-configured", report["refusals"]!.AsArray().Select(r => (string)r!));
    }

    [Fact]
    public void OfflineStorageIsMarkedUnreachable()
    {
        string text = DiagnosticSummary.Build(Full() with { CoordinationReachable = false, Journal = null, Phase = null });
        var report = Parse(text);

        AssertNoCanary(text);
        Assert.Equal("unreachable", (string)report["ownership"]!["status"]!);
        Assert.Contains("storage-offline", report["refusals"]!.AsArray().Select(r => (string)r!));
        // Pending copies come from this PC's own state, so an outage does not hide them.
        Assert.Equal(["target-1"], report["replication"]!["pendingCloudCopies"]!.AsArray().Select(t => (string)t!));
        Assert.Equal("under 7 days", (string)report["replication"]!["lastVerifiedAgo"]!);
        Assert.Contains("cloud-copy-pending", report["refusals"]!.AsArray().Select(r => (string)r!));
    }

    [Fact]
    public void AnUnsupportedSchemaAndUnreviewedBuildAreRefusalCodes()
    {
        var report = Parse(DiagnosticSummary.Build(Full() with
        {
            Compatibility = new MedalCompatibilityReport(new("9999.1.0", "9.9.9"), false, 5, 9, new string('c', 64), false,
                ["Unsupported library schema: CanaryReason"]),
            SignedInAccount = "123123123",
            SessionFits = false,
            RclonePresent = false,
            WinFspInstalled = false,
        }));

        Assert.Equal(
            ["cache-space-insufficient", "cloud-copy-pending", "medal-account-mismatch", "medal-build-unreviewed", "medal-executable-missing",
                "medal-schema-unsupported", "rclone-missing", "recovery-required", "winfsp-missing"],
            report["refusals"]!.AsArray().Select(r => (string)r!));
        Assert.False((bool)report["medal"]!["schemaSupported"]!);
        Assert.Equal(5, (long)report["medal"]!["schemaVersion"]!);
    }

    [Theory]
    [InlineData("2644.591.0 CanaryHost")]
    [InlineData(@"C:\CanaryTools\Medal")]
    [InlineData("canaryuser")]
    public void UnexpectedVersionTextIsNotCopied(string version)
    {
        string text = DiagnosticSummary.Build(Full() with
        {
            ToolVersion = version,
            WindowsVersion = version,
            Compatibility = new MedalCompatibilityReport(new(version, version), true, 4, 3, "CanaryHost fingerprint", true, []),
        });
        var report = Parse(text);

        AssertNoCanary(text);
        Assert.Equal("unrecognized", (string)report["medal"]!["app"]!);
        Assert.Null(report["medal"]!["schemaFingerprint"]);
    }

    [Fact]
    public void AnyKnownPrivateValueIsScrubbedFromAllowedFieldsToo()
    {
        // A value that happens to look like a version is still removed when it matches a private value.
        var report = Parse(DiagnosticSummary.Build(Full() with { WindowsVersion = "10.0." + AccountId }));

        Assert.Equal("10.0.[redacted]", (string)report["windows"]!["version"]!);
    }

    [Fact]
    public void TokensAreNewForEachExport()
    {
        var first = Parse(DiagnosticSummary.Build(Full()));
        var second = Parse(DiagnosticSummary.Build(Full() with
        {
            Config = Config() with { LibraryId = new string('1', 32) },
            Device = new(OtherDevice, "Second"),
            Journal = Full().Journal! with { Owner = new(ThisDevice, "First"), LibraryId = new string('1', 32) },
        }));

        Assert.Equal((string)first["configuration"]!["library"]!, (string)second["configuration"]!["library"]!);
        Assert.Equal("pc-1", (string)second["ownership"]!["thisPc"]!);
        Assert.Equal("pc-2", (string)second["ownership"]!["owner"]!);
    }

    [Fact]
    public void ThisPcOwningTheLibraryIsReportedAsItself()
    {
        var report = Parse(DiagnosticSummary.Build(Full() with
        {
            Journal = Full().Journal! with { State = OwnershipState.Active, Owner = new(ThisDevice, "CanaryHost (canaryuser)") },
        }));

        Assert.Equal("pc-1", (string)report["ownership"]!["owner"]!);
        Assert.Equal("ACTIVE", (string)report["ownership"]!["state"]!);
    }

    [Fact]
    public void PreviewSavesNothingAndSaveWritesExactlyWhatIsShownToANewFileOnly()
    {
        string report = DiagnosticSummary.Build(Full());
        var preview = new StringWriter();
        Assert.Equal(0, DiagnosticSummary.Write(report, null, preview));
        Assert.StartsWith(report, preview.ToString(), StringComparison.Ordinal);
        Assert.Contains("Preview only; nothing was saved or sent.", preview.ToString(), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_fixture.PathFor()));

        string path = _fixture.PathFor("summary.json");
        var saved = new StringWriter();
        Assert.Equal(0, DiagnosticSummary.Write(report, path, saved));
        Assert.Equal(report + "\n", File.ReadAllText(path));
        Assert.StartsWith(report, saved.ToString(), StringComparison.Ordinal);

        File.WriteAllText(path, "keep");
        Assert.Throws<IOException>(() => DiagnosticSummary.Write(report, path, new StringWriter()));
        Assert.Equal("keep", File.ReadAllText(path));
    }
}
