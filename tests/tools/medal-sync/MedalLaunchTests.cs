using System.Diagnostics;
using System.Runtime.Versioning;
using MedalSync.Cli;
using MedalSync.Coordination;
using MedalSync.Platform;
using MedalSync.Tests.Fixtures;

namespace MedalSync.Tests;

[SupportedOSPlatform("windows")]
public sealed class MedalLaunchTests : IDisposable
{
    private readonly MedalFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private MedalInstall Install()
    {
        string executable = _fixture.PathFor("Medal.exe");
        File.WriteAllText(executable, "synthetic; never executed");
        return new MedalInstall(_fixture.Root, _fixture.PathFor("unused.db"), MedalFixture.AccountId,
            executable, new MedalVersions(null, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("medal://clip/abc?x=1&next=two")]
    public void InitialLaunchPassesTheLinkDirectlyWithoutASecondProcess(string? link)
    {
        if (!OperatingSystem.IsWindows()) return;
        var calls = new List<ProcessStartInfo>();
        var install = Install();
        new MedalProcess(install, link, calls.Add).Launch();
        var request = Assert.Single(calls);
        Assert.Equal(install.Executable, request.FileName);
        Assert.True(request.UseShellExecute);
        Assert.Equal(_fixture.Root, request.WorkingDirectory);
        Assert.Equal(link is null ? "" : $"\"{link}\"", request.Arguments);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("medal://a\" --inject")]
    [InlineData("medal://a\nb")]
    public void InvalidLinksNeverReachTheProcessStarter(string link)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Throws<UnsafeOperationException>(() => new MedalProcess(Install(), link,
            _ => Assert.Fail("An invalid link must never launch a process.")).Launch());
        Assert.Throws<UnsafeOperationException>(() => Launcher.LaunchArguments(new LauncherRequest(false, link)));
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(1, CommandRunner.Run(["launch", "--medal-link", link], output, error));
        Assert.Contains("Only a medal:// link", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsLaunchFailureBecomesAnActionableRefusal()
    {
        if (!OperatingSystem.IsWindows()) return;
        var install = Install();
        var error = Assert.Throws<UnsafeOperationException>(() => new MedalProcess(install, start:
            _ => throw new System.ComponentModel.Win32Exception("Synthetic start failure")).Launch());
        Assert.Contains(install.Executable, error.Message, StringComparison.Ordinal);
        Assert.Contains("session and cache are retained", error.Message, StringComparison.Ordinal);
    }
}
