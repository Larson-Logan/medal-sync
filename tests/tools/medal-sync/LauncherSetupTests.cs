using MedalSync.Cli;
using MedalSync.Config;
using MedalSync.Library;
using MedalSync.Tests.Fixtures;

namespace MedalSync.Tests;

public sealed class LauncherSetupTests : IDisposable
{
    private readonly MedalFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private sealed class Ui : ILauncherUi
    {
        public List<string> Messages { get; } = [];
        public void Show(string message) => Messages.Add(message);
        public void Notify(string message) => throw new InvalidOperationException("No session notifications during setup.");
        public bool ConfirmLibraryChanges(LibraryChangeReview review) => throw new InvalidOperationException("No library access during setup.");
        public bool AcceptConfiguration { get; init; }
        public bool OfferConfiguration(string message) { Messages.Add(message); return AcceptConfiguration; }
    }

    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(false, true, 3)]
    [InlineData(false, false, null)]
    public void RepeatedSignInLeavesAnExistingSessionAndItsWindowAlone(bool medal, bool supervisor, int? code)
    {
        var ui = new Ui();
        Assert.Equal(code, Launcher.HandleExistingSession(Launcher.Parse(["--at-sign-in"]), medal, supervisor,
            _ => Assert.Fail("Sign-in must not reopen Medal."), ui));
        Assert.Empty(ui.Messages);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("medal://clip/abc?x=1")]
    public void ManualOpenForwardsExactlyOnceToTheSupervisedSession(string? link)
    {
        var opened = new List<string?>();
        var ui = new Ui();
        Assert.Equal(0, Launcher.HandleExistingSession(new LauncherRequest(false, link), true, true, opened.Add, ui));
        Assert.Equal(link, Assert.Single(opened));
        Assert.Empty(ui.Messages);
    }

    [Fact]
    public void ManualOpenWhileFinishingExplainsWhyItMustWait()
    {
        var ui = new Ui();
        Assert.Equal(3, Launcher.HandleExistingSession(Launcher.Parse([]), false, true,
            _ => Assert.Fail("Must not reopen Medal while finishing."), ui));
        Assert.Equal(Launcher.BusyMessage, Assert.Single(ui.Messages));
    }

    [Fact]
    public void ReopeningFailureShowsItsRecoveryMessageWithoutACrash()
    {
        var ui = new Ui();
        const string message = "Windows could not open Medal. Check the installation and retry.";
        Assert.Equal(1, Launcher.HandleExistingSession(Launcher.Parse([]), true, true,
            _ => throw new UnsafeOperationException(message), ui));
        Assert.Equal(message, Assert.Single(ui.Messages));
    }

    [Fact]
    public void AnUnsupervisedRunningMedalStillGoesThroughTheCoordinator()
    {
        var ui = new Ui();
        Assert.Null(Launcher.HandleExistingSession(Launcher.Parse(["medal://clip/abc"]), true, false,
            _ => Assert.Fail("A link must not bypass the session guards."), ui));
        Assert.Empty(ui.Messages);
    }

    [Fact]
    public void MissingConfigurationExplainsTheNextStepWithoutCreatingState()
    {
        var state = new LocalState(_fixture.PathFor("absent"));
        var ui = new Ui();
        Assert.Equal(1, Launcher.CheckConfiguration(Launcher.Parse([]), state, ui));
        string message = Assert.Single(ui.Messages);
        Assert.Contains(state.ConfigPath, message, StringComparison.Ordinal);
        Assert.Contains("Setup Guide", message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(state.Root));
    }

    [Fact]
    public void UnconfiguredSignInDoesNotShowDialogsOrCreateState()
    {
        var state = new LocalState(_fixture.PathFor("absent"));
        var ui = new Ui();
        Assert.Equal(1, Launcher.CheckConfiguration(Launcher.Parse(["--at-sign-in"]), state, ui));
        Assert.Empty(ui.Messages);
        Assert.False(Directory.Exists(state.Root));
    }

    [Fact]
    public void ConfiguredLaunchContinuesToTheExistingValidationWithoutChangingFiles()
    {
        string root = _fixture.PathFor("state");
        Directory.CreateDirectory(root);
        var state = new LocalState(root);
        File.WriteAllText(state.ConfigPath, "existing validation must still check this");
        var ui = new Ui();
        Assert.Null(Launcher.CheckConfiguration(Launcher.Parse([]), state, ui));
        Assert.Empty(ui.Messages);
        Assert.Equal("existing validation must still check this", File.ReadAllText(state.ConfigPath));
        Assert.False(File.Exists(Path.Combine(root, "device.json")));
    }

    [Fact]
    public void MissingConfigurationPreservesExistingIdentityAndRecoveryEvidence()
    {
        string root = _fixture.PathFor("recover");
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        string device = Path.Combine(root, "device.json");
        string session = Path.Combine(root, "sessions", "retained.json");
        File.WriteAllText(device, "synthetic identity");
        File.WriteAllText(session, "synthetic retained session");
        var ui = new Ui();
        Assert.Equal(1, Launcher.CheckConfiguration(Launcher.Parse([]), new LocalState(root), ui));
        Assert.Contains("recover its original configuration", Assert.Single(ui.Messages), StringComparison.Ordinal);
        Assert.Equal("synthetic identity", File.ReadAllText(device));
        Assert.Equal("synthetic retained session", File.ReadAllText(session));
    }

    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 1)]
    public void ConfigurationWizardRequiresAnExplicitManualChoice(bool signIn, bool accepted, int opened)
    {
        var state = new LocalState(_fixture.PathFor("wizard-absent"));
        var ui = new Ui { AcceptConfiguration = accepted };
        int calls = 0;
        Assert.Equal(1, Launcher.CheckConfiguration(new LauncherRequest(signIn, null), state, ui, target => { Assert.Same(state, target); calls++; }));
        Assert.Equal(opened, calls);
        Assert.False(Directory.Exists(state.Root));
    }
}
