using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MedalSync.Session;

namespace MedalSync.Platform;

/// <summary>
/// Asks Windows to keep the system (not the display) awake while this PC holds work that blocks a handoff.
/// Holds nest: Windows is asked once for the first hold and released with the last. Only the idle timer is
/// affected; user-initiated sleep, shutdown, lid and critical-battery policies still apply. Never throws: a
/// failed request only means the PC may sleep as it would today, and is logged once.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsKeepAwake(Action<string>? log = null) : IKeepAwake, IDisposable
{
    internal const string Reason = "Medal Sync is uploading and verifying clips so another PC can use the library.";
    private readonly object _gate = new();
    private IntPtr _request;
    private int _holds;
    private bool _failed;
    private bool _disposed;

    public IDisposable Hold()
    {
        lock (_gate)
        {
            if (_disposed) return KeepAwakeHold.None;
            if (_holds++ == 0) Set(true);
            return new KeepAwakeHold(Release);
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_holds == 0 || --_holds > 0) return;
            Set(false);
        }
    }

    private void Set(bool awake)
    {
        try
        {
            if (awake)
            {
                if (_request == IntPtr.Zero)
                {
                    var context = new ReasonContext { Version = 0, Flags = PowerRequestContextSimpleString, Reason = Reason };
                    var handle = PowerCreateRequest(ref context);
                    if (handle == IntPtr.Zero || handle == InvalidHandle) { Fail("could not be created", Marshal.GetLastWin32Error()); return; }
                    _request = handle;
                }
                if (!PowerSetRequest(_request, PowerRequestSystemRequired)) Fail("was refused", Marshal.GetLastWin32Error());
            }
            else if (_request != IntPtr.Zero && !PowerClearRequest(_request, PowerRequestSystemRequired))
            {
                Fail("could not be cleared", Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            Fail("is unavailable: " + ex.Message, 0);
        }
    }

    private void Fail(string what, int error)
    {
        if (_failed) return;
        _failed = true;
        try { log?.Invoke($"keep awake: the Windows power request {what}{(error == 0 ? "" : $" (error {error})")}; the PC may sleep on its normal power plan."); }
        catch (Exception) { /* Logging is advisory. */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_request == IntPtr.Zero) return;
            if (_holds > 0) PowerClearRequest(_request, PowerRequestSystemRequired);
            _holds = 0;
            CloseHandle(_request);
            _request = IntPtr.Zero;
        }
    }

    private const uint PowerRequestContextSimpleString = 0x1;
    private const int PowerRequestSystemRequired = 1;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string Reason;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(IntPtr request, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(IntPtr request, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
