using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MedalSync.Platform;

/// <summary>Allows background sync when there is CPU capacity, including during ordinary desktop use.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsIdle
{
    private ulong _idle, _total;
    private long _sampleAt;
    private bool _quiet;

    public bool IsIdle()
    {
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
    private struct FileTime
    {
        public uint Low, High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
}
