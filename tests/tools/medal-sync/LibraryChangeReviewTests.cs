using LarsonServer.MedalSync.Cli;
using LarsonServer.MedalSync.Library;
using LarsonServer.MedalSync.Tests.Fixtures;

namespace LarsonServer.MedalSync.Tests;

public sealed class LibraryChangeReviewTests : IDisposable
{
    private readonly MedalFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private LibraryChangeReview Review(int count)
    {
        string db = _fixture.CreateLibrary("review");
        for (int index = 0; index < count; index++)
            MedalFixture.AddContent(db, $"clip-{index}", video: $@"M:\Clips\Clips\{index}.mp4");
        var before = LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId);
        using (var connection = MedalFixture.Open(db)) MedalFixture.Execute(connection, "DELETE FROM contents");
        var after = LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId);
        return new LibraryChangeReview("test-session", 2, before, after, MissingMediaAudit.Compare(before, after).Suspect.ToArray());
    }

    [Fact]
    public void Review_PaginatesEveryEntryRatherThanSilentlyTruncating()
    {
        var review = Review(13);
        var pages = review.Pages();
        Assert.Equal(3, pages.Count);
        foreach (var change in review.Changes)
            Assert.Equal(1, pages.Count(page => page.Contains(change.ContentId + "\n", StringComparison.Ordinal)));
        Assert.Contains("(3/3)", pages[2], StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewReceipt_RetainsExactBeforeAndAcceptedLibraries()
    {
        if (!OperatingSystem.IsWindows()) return;
        var review = Review(1);
        string directory = SessionCommands.SaveReviewedChanges(_fixture.PathFor("receipts"), review);
        Assert.Equal(review.Before.Digest(), LibrarySnapshot.FromCanonicalJson(File.ReadAllBytes(Path.Combine(directory, "before.json"))).Digest());
        Assert.Equal(review.After.Digest(), LibrarySnapshot.FromCanonicalJson(File.ReadAllBytes(Path.Combine(directory, "accepted.json"))).Digest());
        Assert.Contains(review.After.Digest(), File.ReadAllText(Path.Combine(directory, "review.json")), StringComparison.Ordinal);
    }
}
