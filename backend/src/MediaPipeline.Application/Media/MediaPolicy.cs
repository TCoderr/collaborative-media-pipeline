using MediaPipeline.Application.Abstractions;

namespace MediaPipeline.Application.Media;

/// <summary>
/// The accepted media envelope. Kept deliberately narrow: three still-image formats the
/// spiral gallery actually renders. A narrow surface is the cheapest defence available
/// against a decoder vulnerability.
/// </summary>
public sealed class MediaPolicyOptions
{
    public const string SectionName = "Media";

    /// <summary>Hard cap on an uploaded source file. Enforced while streaming, not after.</summary>
    public long MaxUploadBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>
    /// Decoded-pixel ceiling. A few kilobytes of compressed data can expand to gigabytes
    /// of bitmap, so the limit that matters is pixels, not file size.
    /// </summary>
    public long MaxPixels { get; set; } = 16_000_000;

    public int MaxDimension { get; set; } = 12_000;

    /// <summary>Wall-clock budget for decoding and re-encoding a single derivative.</summary>
    public TimeSpan ProcessingTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public int ThumbnailLongEdge { get; set; } = 512;
    public int PreviewLongEdge { get; set; } = 1600;
    public int WebpQuality { get; set; } = 82;
}

/// <summary>A media type Quire accepts, with the signature that proves it.</summary>
public sealed record AcceptedMediaType(string ContentType, string Extension, byte[] Magic, int MagicOffset);

/// <summary>
/// Content sniffing. The declared Content-Type and the filename extension are treated as
/// hints only; the leading bytes decide, because both hints are attacker-controlled.
/// </summary>
public static class MediaSignatures
{
    private static readonly AcceptedMediaType Jpeg = new("image/jpeg", "jpg", [0xFF, 0xD8, 0xFF], 0);
    private static readonly AcceptedMediaType Png = new("image/png", "png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], 0);

    // WebP is RIFF....WEBP - the four bytes at offset 8 are the discriminator.
    private static readonly AcceptedMediaType WebP = new("image/webp", "webp", [0x57, 0x45, 0x42, 0x50], 8);

    /// <summary>Bytes that must be buffered before a decision can be made.</summary>
    public const int SniffLength = 16;

    public static IReadOnlyList<AcceptedMediaType> Accepted { get; } = [Jpeg, Png, WebP];

    public static IReadOnlyList<string> AcceptedContentTypes { get; } =
        Accepted.Select(a => a.ContentType).ToArray();

    /// <summary>
    /// Identifies the media type from its leading bytes, or throws with a permanent
    /// validation error. Never falls back to the declared type.
    /// </summary>
    public static AcceptedMediaType Detect(ReadOnlySpan<byte> head)
    {
        foreach (var candidate in Accepted)
        {
            var end = candidate.MagicOffset + candidate.Magic.Length;
            if (head.Length < end)
            {
                continue;
            }

            if (head.Slice(candidate.MagicOffset, candidate.Magic.Length).SequenceEqual(candidate.Magic))
            {
                // RIFF container check: WEBP at offset 8 is only meaningful with a RIFF header.
                if (candidate.Extension == "webp"
                    && !head[..4].SequenceEqual("RIFF"u8))
                {
                    continue;
                }

                return candidate;
            }
        }

        throw MediaPipelineException.Validation(
            "UNSUPPORTED_MEDIA_TYPE",
            $"Only {string.Join(", ", AcceptedContentTypes)} uploads are supported.");
    }
}
