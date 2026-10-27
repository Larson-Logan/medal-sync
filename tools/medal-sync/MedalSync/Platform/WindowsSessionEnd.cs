using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LarsonServer.MedalSync.Session;

namespace LarsonServer.MedalSync.Platform;

/// <summary>
/// A real hidden top-level window (message-only windows do not receive shutdown
/// broadcasts). Its dedicated pump never performs network/database work. Windows
/// closes ordinary apps first; the supervisor then verifies their exit and drains.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsSessionEnd : IDisposable
{
    internal const uint QueryEndSession = 0x0011;
    internal const uint EndSession = 0x0016;
    internal const uint ShutdownLevel = 0x100;
    private const uint Close = 0x0010;
    private const uint Destroy = 0x0002;
    private const string Reason = "Medal is finishing clip uploads. Unsent clips resume at next sign-in.";
    private readonly SessionShutdown _shutdown;
    private readonly Action<string> _log;
    private readonly Thread _thread;
    private readonly WindowProcedure _procedure;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly string _className = "MedalSync.SessionEnd." + Guid.NewGuid().ToString("N");
    private Exception? _startupError;
    private readonly uint _previousLevel;
    private readonly uint _previousFlags;
    private readonly bool _setOrder;
    internal IntPtr Window { get; private set; }

    public WindowsSessionEnd(SessionShutdown shutdown, Action<string> log, bool setOrder = true)
    {
        _shutdown = shutdown;
        _log = log;
        _procedure = Dispatch;
        _setOrder = setOrder;
        // Lower levels are notified later. Do not change Medal/rclone or global
        // Windows shutdown timeouts. Restore our own priority when supervision ends.
        if (setOrder && (!GetProcessShutdownParameters(out _previousLevel, out _previousFlags)
            || !SetProcessShutdownParameters(ShutdownLevel, 0))) throw new Win32Exception();
        _thread = new Thread(Pump) { IsBackground = true, Name = "medal-session-end" };
        _thread.Start();
        _ready.Wait();
        if (_startupError is not null)
        {
            if (setOrder) SetProcessShutdownParameters(_previousLevel, _previousFlags);
            throw new UnsafeOperationException("Cannot register Windows shutdown handling: " + _startupError.Message);
        }
        SafeLog("shutdown: listener ready (25-second finish budget)");
    }

    private void Pump()
    {
        IntPtr module = GetModuleHandleW(null);
        try
        {
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = module,
                Procedure = _procedure, ClassName = _className,
            };
            if (RegisterClassExW(ref windowClass) == 0) throw new Win32Exception();
            Window = CreateWindowExW(0, _className, "Medal Sync", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
            if (Window == IntPtr.Zero) throw new Win32Exception();
            _ready.Set();
            int result;
            while ((result = GetMessageW(out var message, IntPtr.Zero, 0, 0)) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }
            if (result == -1) SafeLog("shutdown: message pump failed: " + Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _ready.Set();
        }
        finally
        {
            if (Window != IntPtr.Zero) DestroyWindow(Window);
            UnregisterClassW(_className, module);
        }
    }

    private IntPtr Dispatch(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case QueryEndSession:
                // No file IO, prompts, app closing or upload waits in the query.
                // Another app may still cancel Windows shutdown.
                _shutdown.Query();
                ShutdownBlockReasonCreate(window, Reason);
                return new IntPtr(1);
            case EndSession:
                if (wParam == UIntPtr.Zero)
                {
                    _shutdown.CancelQuery();
                    ShutdownBlockReasonDestroy(window);
                    SafeLog("shutdown: canceled by Windows; supervision continues");
                    return IntPtr.Zero;
                }
                _shutdown.RequestEnd();
                // Also handle an ENDSESSION that arrived without a prior query.
                ShutdownBlockReasonCreate(window, Reason);
                // The worker owns all session operations; never run Finish twice.
                bool complete = _shutdown.WaitForCompletion();
                SafeLog(complete ? "shutdown: supervisor finished" : "shutdown: time limit; unfinished work remains on this PC");
                ShutdownBlockReasonDestroy(window);
                return IntPtr.Zero;
            case Close:
                DestroyWindow(window);
                return IntPtr.Zero;
            case Destroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProcW(window, message, wParam, lParam);
        }
    }

    private void SafeLog(string message)
    {
        // No exception may escape a reverse P/Invoke during shutdown.
        try { _log(message); }
        catch (Exception) { /* The journal/cache, not this diagnostic, are the recovery record. */ }
    }

    public void Dispose()
    {
        _shutdown.Complete();
        PostMessageW(Window, Close, UIntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        if (_setOrder) SetProcessShutdownParameters(_previousLevel, _previousFlags);
        GC.KeepAlive(_procedure);
    }

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public WindowProcedure Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessShutdownParameters(uint level, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessShutdownParameters(out uint level, out uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool UnregisterClassW(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint extendedStyle, string className, string name, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetMessageW(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr DispatchMessageW(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr DefWindowProcW(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool PostMessageW(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr window, string reason);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr window);
}
