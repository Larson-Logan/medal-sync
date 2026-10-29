using System.Buffers;
using System.Security.Cryptography;

namespace MedalSync.Util;

internal static class Hashing
{
    private const int ChunkBytes = 1 << 20;

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// Reads in 1 MiB chunks (few round trips on a share) into one pooled buffer, so hashing many files
    /// neither allocates a fresh large buffer per file nor copies through a second stream buffer.
    /// </summary>
    public static string Sha256File(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 0, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, ChunkBytes)) > 0) hash.AppendData(buffer, 0, read);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
