using AirBridge.App;

namespace AirBridge.Tests;

public sealed class LaunchOptionsTests
{
    [Theory]
    [InlineData("--unknown")]
    [InlineData("--fixture")]
    [InlineData("--snapshot")]
    [InlineData("--debug-pipe")]
    public void InvalidOptionsCannotFallThroughToLiveLaunch(string argument) =>
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse([argument]));

    [Fact]
    public void ProfileOptionsCanPrecedeOrFollowSnapshotArguments()
    {
        var directory = Path.Combine(Path.GetTempPath(), "snapshot-profile");
        var options = LaunchOptions.Parse(["--data-dir", directory, "--snapshot-flyout", "image.png", "Dark", "1.5"]);
        Assert.Equal(directory, options.DataDirectory);
        Assert.Equal(["--snapshot-flyout", "image.png", "Dark", "1.5"], options.Arguments);
        Assert.True(options.IsPreview);
    }

    [Fact]
    public void AmbiguousModesAndDuplicateValuesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(["--fixture", "healthy", "--preview"]));
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(["--data-dir", "a", "--data-dir", "b"]));
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(["--fixture", "typo"]));
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(["--debug-pipe", "bad/pipe"]));
    }

    [Fact]
    public void FixtureCredentialsRemainMemoryOnly()
    {
        var first = new MemoryOpenAiCredentialStore();
        var second = new MemoryOpenAiCredentialStore();
        first.Write("fixture-placeholder");
        Assert.True(first.IsConfigured);
        Assert.False(second.IsConfigured);
        first.Delete();
        Assert.False(first.IsConfigured);
    }
}
