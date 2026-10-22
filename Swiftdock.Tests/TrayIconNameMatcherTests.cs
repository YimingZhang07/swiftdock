using Swiftdock.E2E;
using Xunit;

namespace Swiftdock.Tests;

public sealed class TrayIconNameMatcherTests
{
    private const string English = "Swiftdock · Control+Alt+Shift+F11";
    private const string Chinese = "迅启 · Control+Alt+Shift+F11";

    [Theory]
    [InlineData(English, English, true)]
    [InlineData(Chinese, Chinese, true)]
    [InlineData(Chinese + " " + English, English, true)]
    [InlineData(English + " " + Chinese, Chinese, true)]
    [InlineData(Chinese, English, false)]
    [InlineData(English, Chinese, false)]
    [InlineData(English + " " + Chinese, English, false)]
    [InlineData(Chinese + " " + English, Chinese, false)]
    [InlineData(Chinese + English, English, false)]
    [InlineData(English + Chinese, Chinese, false)]
    [InlineData(English + " extra", English, false)]
    [InlineData("swiftdock · Control+Alt+Shift+F11", English, false)]
    [InlineData(null, English, false)]
    [InlineData("", English, false)]
    [InlineData("", "", false)]
    public void OnlyTheExpectedTooltipOrItsSpaceDelimitedSuffixMatches(string? name, string expected, bool matches) =>
        Assert.Equal(matches, TrayIconNameMatcher.Matches(name, expected));
}
