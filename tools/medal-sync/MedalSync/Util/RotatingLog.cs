using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MedalSync.Util;

/// <summary>
/// Plain-text log capped at <c>files</c> × <c>maxBytes</c> (plan: ten 5 MiB files per PC).
/// The supervisor, later launcher runs and other commands write the same file, so a named mutex
/// serializes appends and rotation across processes, and the file is opened with full sharing.
/// Writing never throws: the log is advisory, and a session must outlive a full disk or a locked
/// file. Lines that could not be written are kept (up to <see cref="MaxHeld"/>) and written first next time.
/// </summary>
internal sealed class RotatingLog(string directory, string name = "medal-sync", long maxBytes = 5L << 20, int files = 10)
{
    internal const int MaxHeld = 100;

    private readonly object _gate = new();
    private readonly Queue<string> _held = new();

    /// <summary>Lines waiting for a later write because the last attempt failed.</summary>
    internal int Held
    {
        get { lock (_gate) return _held.Count; }
    }

    public void Write(string message)
    {
        string line = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        lock (_gate)
        {
            _held.Enqueue(line);
            while (_held.Count > MaxHeld) _held.Dequeue();
            try
            {
                using var _ = CrossProcessLock();
                Directory.CreateDirectory(directory);
                string current = Path.Combine(directory, name + ".log");
                try
                {
                    if (File.Exists(current) && new FileInfo(current).Length >= maxBytes) Rotate(current);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file held by an older writer cannot be moved yet; append now and rotate on a later write.
                }
                Append(current, Encoding.UTF8.GetBytes(string.Concat(_held)));
                _held.Clear();
            }
            catch (Exception)
            {
                // Kept in memory for the next write.
            }
        }
    }

    private static void Append(string path, byte[] bytes)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                // Usually another process briefly holding the file without sharing.
                Thread.Sleep(50);
            }
        }
    }

    private void Rotate(string current)
    {
        string Numbered(int n) => Path.Combine(directory, $"{name}.{n}.log");
        if (File.Exists(Numbered(files - 1))) File.Delete(Numbered(files - 1));
        for (int n = files - 2; n >= 1; n--)
        {
            if (File.Exists(Numbered(n))) File.Move(Numbered(n), Numbered(n + 1));
        }
        File.Move(current, Numbered(1));
    }

    /// <summary>Held while appending or rotating. When it cannot be had in time the write goes ahead unguarded.</summary>
    private IDisposable? CrossProcessLock()
    {
        Mutex mutex;
        try
        {
            string key = Path.GetFullPath(Path.Combine(directory, name)).ToUpperInvariant();
            mutex = new Mutex(false, @"Local\MedalSync.Log." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32]);
        }
        catch (Exception)
        {
            return null;
        }
        try
        {
            if (mutex.WaitOne(TimeSpan.FromSeconds(2))) return new MutexHold(mutex);
        }
        catch (AbandonedMutexException)
        {
            // A writer crashed while holding it; it is ours now.
            return new MutexHold(mutex);
        }
        catch (Exception)
        {
            // Fall through: write unguarded.
        }
        mutex.Dispose();
        return null;
    }

    private sealed class MutexHold(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
