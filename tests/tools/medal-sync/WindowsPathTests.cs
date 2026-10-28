using MedalSync.Util;

namespace MedalSync.Tests;

public sealed class WindowsPathTests
{
    [Theory]
    [InlineData(@"m:\Clips\a.mp4", @"M:\Clips\a.mp4")]
    [InlineData(@"M:/Clips/a.mp4", @"M:\Clips\a.mp4")]
    [InlineData(@"M:\Clips\", @"M:\Clips")]
    [InlineData(@"\\192.0.2.10\Data\Media\Videos", @"\\192.0.2.10\Data\Media\Videos")]
    [InlineData(@"M:\", @"M:\")]
    public void Parse_NormalizesSeparatorsAndDriveCase(string input, string expected) =>
        Assert.Equal(expected, WindowsPath.Parse(input).Display);

    [Theory]
    [InlineData("")]
    [InlineData(@"Clips\a.mp4")]
    [InlineData(@"M:Clips")]
    [InlineData(@"M:\Clips\..\Windows")]
    [InlineData(@"M:\Clips\.\a.mp4")]
    [InlineData(@"M:\Clips\a.mp4:hidden")]
    [InlineData(@"M:\Clips\a.mp4.")]
    [InlineData(@"M:\Clips\a.mp4 ")]
    [InlineData(@"M:\Clips\\a.mp4")]
    [InlineData(@"\\?\M:\Clips")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\server")]
    [InlineData(@"\\server\..\Clips")]
    [InlineData(@"\\server\share \Clips")]
    [InlineData(@"\\bad:server\share\Clips")]
    [InlineData(@"\\server\share:hidden\Clips")]
    [InlineData(@"M:\Clips\NUL.mp4")]
    [InlineData(@"M:\Clips\con.txt")]
    [InlineData(@"M:\Clips\AUX .mp4")]
    [InlineData(@"M:\Clips\PRN")]
    [InlineData(@"M:\Clips\COM1.mp4")]
    [InlineData(@"M:\Clips\lpt9.txt")]
    [InlineData(@"M:\Clips\COM¹.mp4")]
    [InlineData(@"M:\Clips\LPT².txt")]
    [InlineData(@"M:\Clips\CONIN$")]
    public void Parse_RefusesAmbiguousPaths(string input) =>
        Assert.False(WindowsPath.TryParse(input, out _, out _));

    [Theory]
    [InlineData(@"..\outside.mp4")]
    [InlineData("../outside.mp4")]
    [InlineData(@"Game\clip.mp4")]
    [InlineData("Game/clip.mp4")]
    [InlineData("NUL.mp4")]
    public void Append_RefusesTraversalAndDeviceAliases(string segment) =>
        Assert.Throws<ArgumentException>(() => WindowsPath.Parse(@"M:\Clips").Append(segment));

    [Theory]
    [InlineData("COM10.mp4")]
    [InlineData("console.mp4")]
    [InlineData("auxiliary.mp4")]
    [InlineData(".hidden.mp4")]
    public void Append_PreservesOrdinaryFilenames(string segment) =>
        Assert.Equal(@"M:\Clips\" + segment, WindowsPath.Parse(@"M:\Clips").Append(segment).Display);

    [Fact]
    public void IsWithin_IsCaseInsensitiveAndSegmentAware()
    {
        var root = WindowsPath.Parse(@"M:\Clips");
        Assert.True(WindowsPath.Parse(@"m:\clips\Game\a.mp4").IsWithin(root));
        Assert.True(root.IsWithin(root));
        Assert.False(WindowsPath.Parse(@"M:\ClipsOld\a.mp4").IsWithin(root));
        Assert.False(WindowsPath.Parse(@"N:\Clips\a.mp4").IsWithin(root));
    }

    [Fact]
    public void Rebase_MovesTheRelativePart()
    {
        var moved = WindowsPath.Parse(@"D:\Clips\Clips\Game\a.mp4")
            .Rebase(WindowsPath.Parse(@"D:\Clips"), WindowsPath.Parse(@"M:\Clips"));
        Assert.Equal(@"M:\Clips\Clips\Game\a.mp4", moved.Display);
        Assert.Throws<ArgumentException>(() => WindowsPath.Parse(@"E:\x").Rebase(WindowsPath.Parse(@"D:\Clips"), WindowsPath.Parse(@"M:\Clips")));
    }

    [Fact]
    public void Equality_UsesTheCaseFoldedKey()
    {
        Assert.Equal(WindowsPath.Parse(@"m:\CLIPS"), WindowsPath.Parse(@"M:\clips"));
        Assert.Equal(WindowsPath.Parse(@"m:\CLIPS").GetHashCode(), WindowsPath.Parse(@"M:\clips").GetHashCode());
    }
}
