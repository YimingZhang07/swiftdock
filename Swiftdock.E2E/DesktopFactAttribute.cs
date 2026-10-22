using Xunit;

namespace Swiftdock.E2E;

// Discovery-time skip works with xUnit v2 and is visible in TRX; no extra skip package is needed.
public sealed class DesktopFactAttribute : FactAttribute
{
    public DesktopFactAttribute() => Skip = Desktop.UnavailableReason();
}

public sealed class DesktopTheoryAttribute : TheoryAttribute
{
    public DesktopTheoryAttribute() => Skip = Desktop.UnavailableReason();
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DesktopCollection
{
    public const string Name = "Swiftdock real desktop";
}
