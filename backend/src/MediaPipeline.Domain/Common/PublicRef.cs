using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MediaPipeline.Domain.Common;

/// <summary>
/// Opaque, prefixed public identifiers (<c>prj_7h2k...</c>).
/// Internal primary keys stay <see cref="Guid"/>s and never leave the process;
/// clients only ever see these, so nothing about row ordering or volume leaks.
/// </summary>
public static class PublicRef
{
    public const string ProjectPrefix = "prj";
    public const string AssetPrefix = "ast";
    public const string VersionPrefix = "ver";
    public const string CommentPrefix = "cmt";
    public const string JobPrefix = "job";
    public const string UserPrefix = "usr";

    // Crockford-style base32 without I, L, O, U — unambiguous when read aloud.
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
    private const int RandomBytes = 15; // 120 bits -> 24 base32 chars

    public static string New(string prefix) => $"{prefix}_{RandomToken()}";

    public static bool TryParse(string? value, string expectedPrefix, out string normalised)
    {
        normalised = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var span = value.AsSpan().Trim();
        if (span.Length is < 5 or > 64)
        {
            return false;
        }

        var underscore = span.IndexOf('_');
        if (underscore <= 0 || !span[..underscore].SequenceEqual(expectedPrefix))
        {
            return false;
        }

        var body = span[(underscore + 1)..];
        if (body.Length is < 8 or > 48)
        {
            return false;
        }

        foreach (var c in body)
        {
            if (!Alphabet.Contains(c, StringComparison.Ordinal))
            {
                return false;
            }
        }

        normalised = span.ToString();
        return true;
    }

    private static string RandomToken()
    {
        Span<byte> bytes = stackalloc byte[RandomBytes];
        RandomNumberGenerator.Fill(bytes);

        var chars = new char[RandomBytes * 8 / 5];
        var bitBuffer = 0;
        var bitCount = 0;
        var index = 0;

        foreach (var b in bytes)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bitCount += 8;
            while (bitCount >= 5)
            {
                bitCount -= 5;
                chars[index++] = Alphabet[(bitBuffer >> bitCount) & 0x1F];
            }
        }

        return new string(chars, 0, index);
    }

    /// <summary>
    /// Deterministic public ref derived from a seed — used only by seed data so
    /// development fixtures are stable across resets.
    /// </summary>
    public static string Deterministic(string prefix, string seed)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        var chars = new char[20];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[hash[i] & 0x1F];
        }

        _ = BinaryPrimitives.ReadUInt32BigEndian(hash);
        return $"{prefix}_{new string(chars)}";
    }
}
