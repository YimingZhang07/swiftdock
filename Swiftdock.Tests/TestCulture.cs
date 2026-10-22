using System.Globalization;
using System.Runtime.CompilerServices;

namespace Swiftdock.Tests;

// Existing validation/message fixtures are Chinese. Isolate their simulated Windows
// UI language; production still captures the actual user's UI culture at startup.
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
    }
}
