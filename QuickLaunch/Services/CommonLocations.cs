using System.IO;
using System.Runtime.InteropServices;
using QuickLaunch.Interop;

namespace QuickLaunch.Services;

internal enum CommonLocation { Desktop, Downloads, Documents, Pictures, Profile }

internal static class CommonLocations
{
    public static string Resolve(CommonLocation location)
    {
        Guid id = location switch
        {
            CommonLocation.Desktop => new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"),
            CommonLocation.Downloads => new("374DE290-123F-4565-9164-39C4925E467B"),
            CommonLocation.Documents => new("FDD39AD0-238F-46AF-ADB4-6C85480369C7"),
            CommonLocation.Pictures => new("33E28130-4E1E-4676-835A-98395C3BC3BB"),
            CommonLocation.Profile => new("5E6C858F-0E22-4760-9AFE-EA3317B67173"),
            _ => throw new ArgumentOutOfRangeException(nameof(location))
        };
        // DONT_VERIFY returns redirected locations even when they have not been created yet.
        int result = NativeMethods.SHGetKnownFolderPath(in id, 0x4000, 0, out nint pointer);
        try
        {
            if (result < 0 || pointer == 0) throw new IOException("无法读取该常用位置。");
            return Marshal.PtrToStringUni(pointer) ?? throw new IOException("无法读取该常用位置。");
        }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
}
