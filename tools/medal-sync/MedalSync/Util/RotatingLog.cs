using System.Globalization;
using System.Text;

namespace LarsonServer.MedalSync.Util;

/// <summary>Plain-text log capped at <c>files</c> × <c>maxBytes</c> (plan: ten 5 MiB files per PC).</summary>
internal sealed class RotatingLog(string directory, string name = "medal-sync", long maxBytes = 5L << 20, int files = 10)
{
    private readonly object _gate = new();

    public void Write(string message)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            string current = Path.Combine(directory, name + ".log");
            if (File.Exists(current) && new FileInfo(current).Length >= maxBytes) Rotate(current);
            string line = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
            File.AppendAllText(current, line, Encoding.UTF8);
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
}
