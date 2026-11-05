using System.Text.Json;
using System.Text.Json.Serialization;

namespace MedalSync.Util;

internal static class Json
{
    /// <summary>Options for state files, manifests and reports.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: false) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    /// <summary>Any malformed input becomes <see cref="InvalidDataException"/>.</summary>
    public static T Deserialize<T>(ReadOnlySpan<byte> bytes, string what)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new InvalidDataException($"{what} is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{what} is not valid: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Reads without blocking a writer's atomic rename-over (state files on the
    /// share are replaced while other PCs may be reading them).
    /// </summary>
    public static T ReadFile<T>(string path, string what)
    {
        byte[] bytes = ReadBytes(path);
        try
        {
            return Deserialize<T>(bytes, what);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"'{path}': {ex.Message}", ex);
        }
    }

    /// <summary>A file's bytes, read with the same sharing as <see cref="ReadFile{T}"/>.</summary>
    public static byte[] ReadBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
