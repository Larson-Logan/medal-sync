using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MedalSync.Cli;
using MedalSync.Library;
using MedalSync.Config;
using MedalSync.Util;

namespace MedalSync.Platform;

/// <summary>Explicit Windows dialogs and passive notices for the independent status view.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsLauncherUi : ILauncherUi
{
    private const string Title = "Medal Sync";
    private const uint IconWarning = 0x30;
    private const uint IconInformation = 0x40;
    private const uint SetForeground = 0x10000;
    private const uint YesNo = 0x4;
    private const uint DefaultSecondButton = 0x100;
    private const int Yes = 6;

    public void Show(string message) => _ = MessageBoxW(IntPtr.Zero, message, Title, IconWarning | SetForeground);

    internal bool Confirm(string message) =>
        MessageBoxW(IntPtr.Zero, message, Title, IconInformation | YesNo | DefaultSecondButton | SetForeground) == Yes;

    public bool OfferConfiguration(string message) => Confirm(message);

    public bool ConfirmLibraryChanges(LibraryChangeReview review)
    {
        var pages = review.Pages();
        return pages.Count > 0 && pages.All(page =>
            MessageBoxW(IntPtr.Zero, page, "Medal - Review removed clips", IconWarning | YesNo | DefaultSecondButton) == Yes);
    }

    // Passive, coalesced notices stay in the status panel. No focus, balloons or modal windows during a game.
    public void Notify(string message)
    {
        try
        {
            var state = LocalState.ForCurrentUser();
            AtomicFile.Replace(Path.Combine(state.Root, "desktop-notice.json"), Json.Serialize(new DesktopNotice(message, DateTimeOffset.UtcNow)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Supervision continues. */ }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
