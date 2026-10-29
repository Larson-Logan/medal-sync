using System.Text.Json;
using MedalSync.Config;
using MedalSync.Library;

namespace MedalSync.Tests;

/// <summary>Keeps the committed templates and pins in step with the code.</summary>
public sealed class RepositoryFilesTests
{
    private static string Repository()
    {
        // CI normalizes compiler source paths to /_/; those are diagnostic
        // locations, not filesystem paths. Find the actual fixture checkout.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "global.json"))
                && Directory.Exists(Path.Combine(directory.FullName, "tools", "medal-sync", "config")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Cannot locate the repository fixture root.");
    }

    private static string ConfigFile(string name) => Path.Combine(Repository(), "tools", "medal-sync", "config", name);

    [Theory]
    [InlineData("medal-sync.example.json")]
    [InlineData("medal-sync.spike.example.json")]
    [InlineData("medal-sync.local.example.json")]
    [InlineData("medal-sync.cloud.example.json")]
    public void ConfigTemplates_LoadAndValidate(string name)
    {
        var config = MedalSyncConfig.Load(ConfigFile(name));
        Assert.Equal(@"M:\Clips", config.Capture.Display);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(60, true)]
    [InlineData(21, true)]
    [InlineData(20, false)]
    [InlineData(5, false)]
    [InlineData(1_000_001, false)]
    public void SpaceWarning_IsOptionalAndMustBeAboveTheReserve(int? warning, bool valid)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(ConfigFile("medal-sync.example.json")))!;
        if (warning is not null) json["mount"]!["spaceWarningGiB"] = warning;
        string path = Path.Combine(Path.GetTempPath(), "medal-sync-tests", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, json.ToJsonString());
            if (!valid)
            {
                Assert.Contains("mount.spaceWarningGiB", Assert.Throws<InvalidDataException>(() => MedalSyncConfig.Load(path)).Message, StringComparison.Ordinal);
                return;
            }
            var config = MedalSyncConfig.Load(path);
            Assert.Equal(warning, config.Mount.SpaceWarningGiB);
            // An unset warning is not written back, so older builds can still read a saved configuration.
            Assert.Equal(warning is not null, Util.Json.Serialize(config.Mount).AsSpan().IndexOf("spaceWarningGiB"u8) >= 0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SpikeTemplate_PointsAtADisposableRemote()
    {
        var spike = MedalSyncConfig.Load(ConfigFile("medal-sync.spike.example.json"));
        var real = MedalSyncConfig.Load(ConfigFile("medal-sync.example.json"));
        Assert.Contains("spike", spike.RemoteRoot.Segments[^1], StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(real.Remote, spike.Remote);
        Assert.NotEqual(real.Mount.ControlPort, spike.Mount.ControlPort);
        Assert.Empty(spike.SyncExclusions);
    }

    [Fact]
    public void DependencyPins_AreCompleteSha256Digests()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ConfigFile("dependencies.json")));
        foreach (string name in new[] { "rclone", "winfsp" })
        {
            var pin = document.RootElement.GetProperty(name);
            string sha = pin.GetProperty("sha256").GetString()!;
            Assert.Matches("^[0-9a-f]{64}$", sha);
            Assert.StartsWith("https://", pin.GetProperty("url").GetString(), StringComparison.Ordinal);
            Assert.EndsWith(pin.GetProperty("asset").GetString()!, pin.GetProperty("url").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SchemaFixture_IsSchemaOnly()
    {
        string sql = File.ReadAllText(Path.Combine(Repository(), "tests", "tools", "medal-sync", "Fixtures", "medal-schema-v4.sql"));
        Assert.DoesNotContain("INSERT INTO contents", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT INTO key_values", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Single(MedalSchema.SupportedFingerprints);
    }
}
