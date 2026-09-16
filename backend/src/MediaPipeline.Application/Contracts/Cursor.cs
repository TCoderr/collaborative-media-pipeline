using System.Buffers.Text;
using System.Globalization;
using System.Text;
using MediaPipeline.Application.Abstractions;

namespace MediaPipeline.Application.Contracts;

/// <summary>
/// Keyset pagination cursor: the sort key of the last row on a page.
/// <para>
/// Opaque to clients but not secret - it only encodes a timestamp and an id the caller has
/// already seen. Offsets were rejected because they skip or duplicate rows when the
/// underlying list changes between pages, which it constantly does here.
/// </para>
/// </summary>
public readonly record struct CursorValue(DateTimeOffset CreatedAt, Guid Id);

public static class Cursor
{
    public static string Encode(DateTimeOffset createdAt, Guid id)
    {
        var raw = $"{createdAt.UtcTicks:D19}:{id:N}";
        return Base64Url(Encoding.UTF8.GetBytes(raw));
    }

    public static CursorValue? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        try
        {
            var bytes = FromBase64Url(cursor);
            var raw = Encoding.UTF8.GetString(bytes);
            var separator = raw.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw MediaPipelineException.Validation("INVALID_CURSOR", "The pagination cursor is not valid.");
            }

            var ticks = long.Parse(raw[..separator], CultureInfo.InvariantCulture);
            var id = Guid.ParseExact(raw[(separator + 1)..], "N");
            return new CursorValue(new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            throw MediaPipelineException.Validation("INVALID_CURSOR", "The pagination cursor is not valid.");
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        return Convert.FromBase64String(padded);
    }

    static Cursor() => _ = Base64.IsValid("AA==");
}

/// <summary>
/// Maps PostgreSQL's <c>xmin</c> to the ETag-shaped token clients send back on updates.
/// </summary>
public static class ConcurrencyToken
{
    public static string Format(uint rowVersion) => rowVersion.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Rejects an update whose token does not match the current row. Absent tokens are
    /// allowed for endpoints where a blind write is acceptable; callers decide by passing
    /// null or not.
    /// </summary>
    public static void Enforce(string? expected, uint actual)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return;
        }

        var trimmed = expected.Trim().Trim('"');
        if (!uint.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed != actual)
        {
            throw MediaPipelineException.Conflict(
                "CONCURRENT_MODIFICATION", "Someone else changed this while you were editing. Reload and try again.");
        }
    }
}
