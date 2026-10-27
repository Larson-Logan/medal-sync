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
    public void ConfigTemplates_LoadAndValidate(string name)
    {
        var config = MedalSyncConfig.Load(ConfigFile(name));
        Assert.Equal(@"M:\Clips", config.Capture.Display);
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
