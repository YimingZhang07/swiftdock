using System.Runtime.InteropServices;

namespace QuickLaunch.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSize { public int Width, Height; }

[ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    [PreserveSig] int GetImage(NativeSize size, uint flags, out nint bitmap);
}
