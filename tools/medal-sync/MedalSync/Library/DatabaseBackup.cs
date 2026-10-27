using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MedalSync.Library;

/// <summary>
/// Rollback copies of a whole Medal database, taken with SQLite's online
/// backup API so the WAL is included. Only files this class names are pruned.
/// </summary>
internal static class DatabaseBackup
{
    private const string Prefix = "medal-library-";
    private const string StampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    public static string Create(SqliteConnection source, string directory, string accountId, DateTimeOffset now)
    {
        Directory.CreateDirectory(directory);
        string stamp = now.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture);
        // The random suffix keeps two backups in the same millisecond apart.
        string path = Path.Combine(directory, $"{Prefix}{accountId}-{stamp}-{Guid.NewGuid().ToString("N")[..8]}.db");
        if (File.Exists(path)) throw new IOException($"Backup '{path}' already exists.");
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString()))
        {
            destination.Open();
            source.BackupDatabase(destination);
            // The copy inherits WAL mode; make it one self-contained file.
            LibraryDatabase.Execute(destination, null, "PRAGMA journal_mode = DELETE");
        }
        Verify(path);
        return path;
    }

    /// <summary>Opens a backup read-only and requires a clean integrity check.</summary>
    public static void Verify(string path)
    {
        using var connection = LibraryDatabase.OpenReadOnly(path);
        string result = MedalSchema.Scalar<string>(connection, null, "PRAGMA integrity_check");
        if (result != "ok") throw new InvalidDataException($"Backup '{path}' failed its integrity check: {result}");
    }

    /// <summary>Keeps the newest <paramref name="keep"/> backups of this account.</summary>
    public static IReadOnlyList<string> Prune(string directory, string accountId, int keep)
    {
        if (keep < 1) throw new ArgumentOutOfRangeException(nameof(keep), "At least one backup must be kept.");
        if (!Directory.Exists(directory)) return [];
        var removed = new List<string>();
        foreach (string path in Directory.EnumerateFiles(directory, $"{Prefix}{accountId}-*.db")
            .Where(p => IsOurs(Path.GetFileName(p), accountId))
            .OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal)
            .Skip(keep))
        {
            File.Delete(path);
            foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
            {
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            removed.Add(path);
        }
        return removed;
    }

    public static IReadOnlyList<string> List(string directory, string accountId) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, $"{Prefix}{accountId}-*.db")
                .Where(p => IsOurs(Path.GetFileName(p), accountId))
                .OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal)
                .ToList()
            : [];

    private static bool IsOurs(string name, string accountId)
    {
        string start = $"{Prefix}{accountId}-";
        if (!name.StartsWith(start, StringComparison.Ordinal) || !name.EndsWith(".db", StringComparison.Ordinal)
            || name.Length <= start.Length + ".db".Length)
        {
            return false;
        }
        string[] parts = name[start.Length..^".db".Length].Split('-');
        if (parts.Length != 2 || parts[1].Length != 8 || !parts[1].All(char.IsAsciiHexDigitLower)) return false;
        string stamp = parts[0];
        return DateTime.TryParseExact(stamp, StampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _);
    }
}
