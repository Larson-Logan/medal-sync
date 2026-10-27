using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LarsonServer.MedalSync.Cli;
using LarsonServer.MedalSync.Library;

namespace LarsonServer.MedalSync.Platform;

/// <summary>Plain Windows message boxes; no UI framework is bundled.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsLauncherUi : ILauncherUi
{
    private const string Title = "Medal (medal-sync)";
    private const uint IconWarning = 0x30;
    private const uint IconInformation = 0x40;
    private const uint SetForeground = 0x10000;
    private const uint YesNo = 0x4;
    private const uint DefaultSecondButton = 0x100;
    private const int Yes = 6;

    public void Show(string message) => _ = MessageBoxW(IntPtr.Zero, message, Title, IconWarning | SetForeground);

    public bool ConfirmLibraryChanges(LibraryChangeReview review)
    {
        var pages = review.Pages();
        return pages.Count > 0 && pages.All(page =>
            MessageBoxW(IntPtr.Zero, page, "Medal - Review removed clips", IconWarning | YesNo | DefaultSecondButton) == Yes);
    }

    // No SetForeground: a box that took focus could pull the user out of a full-screen game.
    public void Notify(string message) =>
        new Thread(() => _ = MessageBoxW(IntPtr.Zero, message, Title, IconInformation)) { IsBackground = true, Name = "medal-notice" }.Start();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
