using System.IO;
using QuickLaunch.Snapshots;
using Xunit;

namespace QuickLaunch.Tests;

public sealed class ManifestWriterTests
{
    [Fact]
    public void TransientDestinationLockRetriesWithoutLosingLatestReport()
    {
        int replacements = 0; var delays = new List<int>(); var files = new Dictionary<string, string>();
        var writer = new ManifestWriter((p, text) => files[p] = text, (source, target) =>
        {
            if (++replacements < 3) throw new UnauthorizedAccessException("AV lock");
            files[target] = files[source];
        }, delays.Add);
        Assert.Null(writer.TryWrite("snapshots.json", "latest"));
        Assert.Equal("latest", files["snapshots.json"]); Assert.Equal(new[] { 25, 50 }, delays);
    }

    [Fact]
    public void PersistentRenameLockFallsBackToDirectWrite()
    {
        var files = new Dictionary<string, string>();
        var writer = new ManifestWriter((p, text) => files[p] = text,
            (_, _) => throw new IOException("locked"), _ => { });
        Assert.Null(writer.TryWrite("snapshots.json", "complete"));
        Assert.Equal("complete", files["snapshots.json"]);
    }

    [Fact]
    public void FullyLockedManifestReturnsErrorAndLaterProgressCanStillBeSaved()
    {
        bool locked = true; string? saved = null;
        var writer = new ManifestWriter((_, text) =>
        {
            if (locked) throw new UnauthorizedAccessException("locked");
            saved = text;
        }, (_, _) => { }, _ => { });
        Assert.Contains("direct write", writer.TryWrite("snapshots.json", "19 images"));
        locked = false;
        Assert.Null(writer.TryWrite("snapshots.json", "39 images")); Assert.Equal("39 images", saved);
    }
}
