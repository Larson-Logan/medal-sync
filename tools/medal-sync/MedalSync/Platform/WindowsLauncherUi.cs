using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MedalSync.Cli;
using MedalSync.Library;
using MedalSync.Config;
using MedalSync.Util;

namespace MedalSync.Platform;

/// <summary>Explicit Windows dialogs and passive notices for the independent status view.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsLauncherUi(Func<HandoffWaitOffer, HandoffAction>? chooseHandoff = null) : ILauncherUi
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

    /// <summary>No is the default: waiting, and later opening Medal by itself, needs an explicit Yes.</summary>
    public HandoffChoice OfferHandoffWait(HandoffWaitOffer offer)
    {
        var action = chooseHandoff?.Invoke(offer) ?? (Confirm($"{offer.Message}\n\nWait here and open Medal automatically once the other PC hands off the library? "
            + $"Medal Sync keeps checking for up to {Session.HandoffWaitUpdate.Duration(offer.Timeout)}, and you can cancel at any time.")
            ? HandoffAction.Wait : HandoffAction.Cancel);
        if (action == HandoffAction.UseLastSync && offer.Recovery is { } journal)
        {
            string owner = journal.Owner!.DisplayName;
            return new(UseLastSync: Confirm($"Has {owner} fully shut down? A sleeping or disconnected PC can resume writing.\n\n"
                + $"Medal will open here from shared generation {journal.CommittedGeneration}. Clips and edits that had yet to sync stay on {owner}."));
        }
        if (action != HandoffAction.Wait) return new();
        try { return new(new WindowsHandoffWaitView(offer.Timeout, Show)); }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException or MarshalDirectiveException or TypeLoadException)
        {
            // Without a visible Cancel there is no wait.
            Show("The waiting window could not be shown, so Medal Sync is not waiting. Open Medal again after the other PC reaches Ready.");
            return new();
        }
    }

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
