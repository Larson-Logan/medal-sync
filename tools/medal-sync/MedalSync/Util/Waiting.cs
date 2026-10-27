using LarsonServer.MedalSync.Session;

namespace LarsonServer.MedalSync.Util;

internal static class Waiting
{
    /// <summary>Polls <paramref name="ready"/> until it is true or the timeout passes; returns the last answer.</summary>
    public static bool Until(Func<bool> ready, TimeSpan timeout, TimeSpan interval, IClock clock)
    {
        var deadline = clock.Now + timeout;
        while (true)
        {
            if (ready()) return true;
            if (clock.Now >= deadline) return false;
            clock.Sleep(interval);
        }
    }
}
