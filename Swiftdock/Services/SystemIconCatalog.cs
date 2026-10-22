using System.IO;
using System.Runtime.InteropServices;

namespace Swiftdock.Services;

public sealed record CatalogIcon(UiText Label, string Spec) { public string Name => Label.Value; }

// Only resource metadata is enumerated here. Bitmaps use IconService on demand.
internal static partial class SystemIconCatalog
{
    internal static readonly string[] Libraries = [@"%SystemRoot%\System32\imageres.dll", @"%SystemRoot%\System32\shell32.dll"];

    internal static string PortablePath(string path)
    {
        string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        string root = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(Path.DirectorySeparatorChar);
        return root.Length > 0 && full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? "%SystemRoot%" + full[root.Length..] : ItemIdentity.PortablePath(full);
    }

    internal static IReadOnlyList<CatalogIcon> Enumerate()
    {
        List<CatalogIcon> icons = [];
        foreach (string library in Libraries)
        {
            // Data/image resources only: never execute a DLL's entry point.
            nint module = LoadLibraryEx(Environment.ExpandEnvironmentVariables(library), 0, 0x22);
            if (module == 0) continue;
            try
            {
                EnumResourceName callback = (_, _, name, _) =>
                {
                    // ExtractIcon's negative-index convention addresses integer IDs.
                    if ((nuint)name is > 0 and <= ushort.MaxValue)
                    {
                        int id = (int)name;
                        icons.Add(new(L.Text("SystemIcon", Path.GetFileName(library), id), $"file:{library},-{id}"));
                    }
                    return true;
                };
                _ = EnumResourceNames(module, 14 /* RT_GROUP_ICON */, callback, 0);
                GC.KeepAlive(callback);
            }
            finally { _ = FreeLibrary(module); }
        }
        return icons;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumResourceName(nint module, nint type, nint name, nint parameter);
    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryEx(string path, nint file, uint flags);
    [LibraryImport("kernel32.dll", EntryPoint = "EnumResourceNamesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumResourceNames(nint module, nint type, EnumResourceName callback, nint parameter);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(nint module);
}
