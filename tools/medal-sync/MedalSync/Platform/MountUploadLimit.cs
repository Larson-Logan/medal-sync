using System.Text.Json;
using MedalSync.Config;
using MedalSync.Storage;

namespace MedalSync.Platform;

/// <summary>
/// Switches the running clip drive between the recording and finishing upload limits through rclone's
/// <c>core/bwlimit</c> call. A limit only changes speed: a failed switch is reported and never stops a session.
/// </summary>
internal sealed class MountUploadLimit(MountConfig mount)
{
    /// <summary>The rate the drive is known to use; null when unknown, such as after attaching to a running drive.</summary>
    private string? _rate;

    /// <summary>Records the rate a newly started drive was given on its command line.</summary>
    public void Started(bool forRecording) => _rate = MountPlan.Rate(mount.UploadLimitMiB(forRecording));

    /// <summary>Forgets the known rate, so the next <see cref="Apply"/> sets it again.</summary>
    public void Reset() => _rate = null;

    /// <summary>
    /// Sets the limit for recording or finishing when the drive may be using another one. Null when it is in place;
    /// otherwise what to tell the user. Never throws; the next call retries a failed switch.
    /// </summary>
    public string? Apply(RcloneControl? control, bool recording)
    {
        string rate = MountPlan.Rate(mount.UploadLimitMiB(recording));
        if (_rate == rate) return null;
        string? answer = control?.Call("core/bwlimit", JsonSerializer.Serialize(new Dictionary<string, string> { ["rate"] = rate }),
            TimeSpan.FromSeconds(5));
        if (answer is null)
        {
            string limit = rate == "off" ? "unlimited" : $"{rate[..^2]} MiB/s";
            return recording
                ? $"The clip drive's upload limit could not be set to {limit} for recording. Recording continues; uploads may use more bandwidth until it is set."
                : $"The clip drive's upload limit could not be set to {limit} for finishing. Uploads continue at the recording limit.";
        }
        _rate = rate;
        return null;
    }
}
