using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MedalSync.Platform;

/// <summary>Conservative background-work gate: no input for a minute and less than 20 percent CPU use.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsIdle
{
    private ulong _idle, _total;
    private long _sampleAt;
    private bool _quiet;

    public bool IsIdle()
    {
        var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref input) || unchecked((uint)Environment.TickCount - input.Tick) < 60_000) return false;
        long now = Environment.TickCount64;
        if (now - _sampleAt < 2_000) return _quiet;
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return false;
        ulong total = kernel.Value + user.Value;
        _quiet = _sampleAt != 0 && total > _total && idle.Value >= _idle
            && (double)(idle.Value - _idle) / (total - _total) >= 0.8;
        (_idle, _total, _sampleAt) = (idle.Value, total, now);
        return _quiet;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput { public uint Size, Tick; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low, High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInput input);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
}
