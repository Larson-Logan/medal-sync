using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MedalSync.Platform;

/// <summary>Creates Shell's native progress dialog, used for the update download and for waiting on another PC.</summary>
[SupportedOSPlatform("windows")]
internal static class ShellProgressDialog
{
    public const uint AutoTime = 0x2;
    public const uint NoTime = 0x4;
    public const uint Marquee = 0x20;

    public static IShellProgressDialog Create() =>
        (IShellProgressDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("F8383852-FCD3-11D1-A6B9-006097DF5BD4"), true)!)!;
}

/// <summary>
/// Shell's IProgressDialog. It owns its window and message pump independently of the tray view, but has no
/// cross-apartment marshaling, so only the (STA) thread that created it may call it.
/// </summary>
[ComImport, Guid("EBBC7C04-315E-11D2-B62F-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellProgressDialog
{
    void StartProgressDialog(IntPtr parent, [MarshalAs(UnmanagedType.IUnknown)] object? enableModeless, uint flags, IntPtr reserved);
    void StopProgressDialog();
    void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
    void SetAnimation(IntPtr instance, uint resource);
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool HasUserCancelled();
    void SetProgress(uint completed, uint total);
    void SetProgress64(ulong completed, ulong total);
    void SetLine(uint line, [MarshalAs(UnmanagedType.LPWStr)] string text, [MarshalAs(UnmanagedType.Bool)] bool compactPath, IntPtr reserved);
    void SetCancelMsg([MarshalAs(UnmanagedType.LPWStr)] string message, IntPtr reserved);
}
