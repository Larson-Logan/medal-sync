using System.Text.Json.Nodes;
using MedalSync.Cli;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

public sealed class ConfigurationNullTests : IDisposable
{
    private readonly MedalFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private static MedalSyncConfig ValidConfig => new(1, "0123456789abcdef0123456789abcdef", "12345",
        @"\\server\Data\Coordination", @"\\server\Data\Media", @"M:\Clips", @"E:\Cache", @"C:\Tools\rclone.exe",
        new(5, 20, 20, 300, 30, 1000, 2, 53682), 4, []);

    public static IEnumerable<object[]> MissingRequiredStrings()
    {
        foreach (string field in new[] { "libraryId", "accountId", "coordinationRoot", "remote", "captureFolder", "cacheRoot", "rclonePath" })
            foreach (string value in new[] { "null", "omitted", "empty", "whitespace" })
                yield return [field, value];
    }

    [Theory]
    [MemberData(nameof(MissingRequiredStrings))]
    public void RequiredStringFieldsRefuseThroughTheRealConfigCommand(string field, string value)
    {
        var document = JsonNode.Parse(Json.Serialize(ValidConfig))!.AsObject();
        if (value == "omitted") document.Remove(field);
        else document[field] = value == "null" ? null : value == "empty" ? "" : " ";
        string path = _fixture.PathFor("config.json");
        File.WriteAllText(path, document.ToJsonString());
        byte[] original = File.ReadAllBytes(path);

        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(1, CommandRunner.Run(["config", "validate", "--config", path], output, error));
        Assert.Contains("configuration is invalid", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(field, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
        var direct = Assert.Throws<InvalidDataException>(() => MedalSyncConfig.Load(path));
        Assert.Contains(field, direct.Message, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void MultipleMissingFieldsAreReportedBeforeDerivedPathsAreAccessed()
    {
        var document = JsonNode.Parse(Json.Serialize(ValidConfig))!.AsObject();
        foreach (string field in new[] { "libraryId", "accountId", "coordinationRoot", "remote", "captureFolder", "cacheRoot", "rclonePath" })
            document[field] = null;
        string path = _fixture.PathFor("config.json");
        File.WriteAllText(path, document.ToJsonString());
        var error = Assert.Throws<InvalidDataException>(() => MedalSyncConfig.Load(path));
        Assert.Contains("libraryId", error.Message, StringComparison.Ordinal);
        Assert.Contains("captureFolder", error.Message, StringComparison.Ordinal);
        Assert.Contains("rclonePath", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingIdentityIsInvalidBeforeRegexEvaluation(string? value) =>
        Assert.False(CoordinationStore.IsValidId(value));
}
