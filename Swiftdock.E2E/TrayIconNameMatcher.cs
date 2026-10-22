namespace Swiftdock.E2E;

internal static class TrayIconNameMatcher
{
    // Windows 11 Explorer can concatenate the original and updated tooltips in the overflow icon's UIA Name.
    internal static bool Matches(string? name, string expectedTooltip) =>
        !string.IsNullOrEmpty(expectedTooltip) && name is not null
        && (string.Equals(name, expectedTooltip, StringComparison.Ordinal)
            || name.EndsWith(" " + expectedTooltip, StringComparison.Ordinal));
}
