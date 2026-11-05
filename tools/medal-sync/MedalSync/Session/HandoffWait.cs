using System.Globalization;
using MedalSync.Cli;
using MedalSync.Coordination;

namespace MedalSync.Session;

/// <summary>Why a launch returned <see cref="LaunchStatus.Waiting"/> in a way an explicit handoff wait may keep polling.</summary>
internal enum LaunchWait
{
    /// <summary>Not a handoff wait: started, refused, shutting down, or this PC's own session must finish first.</summary>
    None,

    /// <summary>Another PC owns the library and has not released it. Nothing was acquired.</summary>
    OwnedElsewhere,

    /// <summary>The journal could not be read, or the switch request written, before anything was acquired.</summary>
    ServerUnreachable,
}

/// <summary>
/// An explicitly chosen wait for another PC to hand off the library. Release is only ever a Clean journal read
/// from storage; elapsed time, a missing lock or an unreachable owner never count as one.
/// </summary>
/// <param name="Cancelled">Polled on the waiting thread between checks; true stops the wait before Medal starts.</param>
/// <param name="Update">Receives what this PC last saw after every check. Advisory: a failure never changes the wait.</param>
internal sealed record HandoffWait(TimeSpan Timeout, Func<bool>? Cancelled = null, Action<HandoffWaitUpdate>? Update = null)
{
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromHours(1);

    /// <summary>Parses <c>--wait-for-handoff SECONDS</c>: null when absent, otherwise 1 second to <see cref="MaxTimeout"/>.</summary>
    public static TimeSpan? ParseSeconds(string? value)
    {
        if (value is null) return null;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)
            && seconds > 0 && seconds <= MaxTimeout.TotalSeconds
            ? TimeSpan.FromSeconds(seconds)
            : throw new UsageException($"--wait-for-handoff takes a whole number of seconds from 1 to {MaxTimeout.TotalSeconds:0}.");
    }

    /// <summary>The first interval between checks, and the interval again after the owner's state changes.</summary>
    public TimeSpan FirstPoll { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Checks back off to this interval while nothing changes or the server stays unreachable.</summary>
    public TimeSpan MaxPoll { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often cancellation and Windows shutdown are checked between polls.</summary>
    public TimeSpan Slice { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// What the waiting PC last saw. <paramref name="Fresh"/> is false when the latest check could not reach the journal,
/// so <paramref name="Owner"/> and <paramref name="State"/> are only what was seen at <paramref name="SeenAt"/>.
/// </summary>
internal sealed record HandoffWaitUpdate(DeviceIdentity? Owner, OwnershipState? State, DateTimeOffset? SeenAt, bool Fresh, TimeSpan Remaining)
{
    public string Describe()
    {
        string? seen = Owner is { } owner && State is { } state && SeenAt is { } at
            ? $"{owner.DisplayName} has the library ({Ownership.Describe(state)}), checked at {Clock(at)}"
            : null;
        if (Fresh)
        {
            return seen is null
                ? "Another PC took the library first. Still waiting for it to hand off."
                : $"{seen}. Quit Medal there; this PC opens Medal once the handoff is complete.";
        }
        return seen is null
            ? "The shared library's server cannot be reached. Still waiting; nothing was changed."
            : $"The shared library's server cannot be reached. Still waiting; last seen: {seen}, which may be out of date.";
    }

    /// <summary>Two short lines for a progress dialog, which shows each on one row.</summary>
    public (string Title, string Detail) Lines()
    {
        string who = Owner?.DisplayName ?? "the other PC";
        string title = $"Waiting for {who} to hand off the library";
        string detail = (Fresh, State, SeenAt) switch
        {
            (true, { } state, { } at) => $"{who}: {Ownership.Describe(state)}, checked at {Clock(at)}.",
            (true, _, _) => "Another PC took the library first; still waiting.",
            (false, { } state, { } at) => $"Server unreachable. Last seen {Ownership.Describe(state)} at {Clock(at)}; may be out of date.",
            _ => "Server unreachable; still waiting. Nothing was changed.",
        };
        return (title, detail);
    }

    private static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>A short, readable duration such as "45 seconds" or "15 minutes".</summary>
    internal static string Duration(TimeSpan value)
    {
        int seconds = (int)Math.Round(value.TotalSeconds);
        if (seconds < 120) return seconds == 1 ? "1 second" : $"{seconds} seconds";
        int minutes = (int)Math.Round(value.TotalMinutes);
        return $"{minutes} minutes";
    }
}
