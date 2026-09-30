using System.Security.Cryptography;

namespace Yav.Core;

/// <summary>Creates short identifiers that sort by creation time and are safe in file names.</summary>
public static class Ids
{
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz"; // Crockford base32, lower case

    /// <summary>Example: <c>20260929-112501-k3f9q</c>.</summary>
    public static string NewRunId(TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        return $"{now:yyyyMMdd}-{now:HHmmss}-{RandomSuffix(5)}";
    }

    public static string NewId(string prefix) => $"{prefix}-{RandomSuffix(12)}";

    public static string RandomSuffix(int length)
    {
        Span<byte> bytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(bytes);
        return string.Create(length, bytes.ToArray(), static (span, state) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = Alphabet[state[i] & 31];
            }
        });
    }

    /// <summary>True when the text has the exact shape produced by <see cref="NewRunId"/>.</summary>
    public static bool IsRunId(string? text)
    {
        if (text is null || text.Length != 21 || text[8] != '-' || text[15] != '-')
        {
            return false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (i is 8 or 15)
            {
                continue;
            }

            var c = text[i];
            var ok = i < 15 ? char.IsAsciiDigit(c) : Alphabet.Contains(c);
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
