using System.Security.Cryptography;
using MediaPipeline.Application.Abstractions;
using MediaPipeline.Application.Media;
using MediaPipeline.Domain.Assets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace MediaPipeline.Infrastructure.Media;

/// <summary>
/// Real image processing on SkiaSharp. Every entry point bounds the work before decoding:
/// the header is read first, dimensions are checked against policy, and only then are
/// pixels materialised - so a small file that claims to be 40000x40000 is rejected rather
/// than allocating gigabytes.
/// </summary>
public sealed class SkiaMediaProcessor(
    IOptions<MediaPolicyOptions> options,
    ILogger<SkiaMediaProcessor> logger) : IMediaProcessor
{
    private readonly MediaPolicyOptions _policy = options.Value;

    public MediaProbe Probe(Stream source)
    {
        using var managed = new SKManagedStream(source, disposeManagedStream: false);
        using var codec = SKCodec.Create(managed)
            ?? throw new PermanentMediaException("MEDIA_UNREADABLE", "The uploaded file could not be decoded as an image.");

        if (codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp) || codec.FrameCount > 1)
            throw new PermanentMediaException("UNSUPPORTED_MEDIA", "Only still JPEG, PNG and WebP images are supported.");
        var info = codec.Info;
        EnsureWithinPolicy(info.Width, info.Height);
        return new MediaProbe(codec.EncodedFormat.ToString(), info.Width, info.Height);
    }

    public async Task<GeneratedDerivative> CreateDerivativeAsync(
        Stream source,
        DerivativeKind kind,
        CancellationToken cancellationToken)
    {
        var longEdge = kind switch
        {
            DerivativeKind.Thumbnail => _policy.ThumbnailLongEdge,
            DerivativeKind.Preview => _policy.PreviewLongEdge,
            _ => _policy.PreviewLongEdge,
        };

        // Native decoding is synchronous. Cancellation is checked between bounded stages;
        // the worker process watchdog enforces the hard wall-clock limit.
        return await Task.Run(() => Encode(source, kind, longEdge, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private GeneratedDerivative Encode(Stream source, DerivativeKind kind, int longEdge, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var managed = new SKManagedStream(source, disposeManagedStream: false);
        using var codec = SKCodec.Create(managed)
            ?? throw new PermanentMediaException("MEDIA_UNREADABLE", "The uploaded file could not be decoded as an image.");

        EnsureWithinPolicy(codec.Info.Width, codec.Info.Height);

        // Ask the codec for a pre-scaled decode where it supports one (JPEG): this avoids
        // materialising the full-resolution bitmap for large sources.
        //
        // Codecs only honour coarse scales (1/2, 1/4, 1/8 for JPEG), so the result can land
        // *below* the target - which would silently ship a smaller derivative than the policy
        // promises. When that happens, decode at full size and let the resampler hit the
        // target exactly; the pixel budget already bounds how big "full size" can be.
        var decodeSize = codec.GetScaledDimensions(ComputeScale(codec.Info.Width, codec.Info.Height, longEdge));
        if (Math.Max(decodeSize.Width, decodeSize.Height) < longEdge)
        {
            decodeSize = new SKSizeI(codec.Info.Width, codec.Info.Height);
        }

        var decodeInfo = new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Rgba8888, SKAlphaType.Premul);

        using var decoded = new SKBitmap(decodeInfo);
        if (codec.GetPixels(decodeInfo, decoded.GetPixels()) != SKCodecResult.Success)
            throw new PermanentMediaException("MEDIA_DECODE_FAILED", "The image is incomplete or could not be decoded.");

        cancellationToken.ThrowIfCancellationRequested();

        var (targetWidth, targetHeight) = FitWithin(decoded.Width, decoded.Height, longEdge);
        using var resized = decoded.Resize(
            new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul),
            new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new PermanentMediaException("MEDIA_RESIZE_FAILED", "The image could not be resized.");

        cancellationToken.ThrowIfCancellationRequested();

        using var image = SKImage.FromBitmap(resized);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, _policy.WebpQuality)
            ?? throw new PermanentMediaException("MEDIA_ENCODE_FAILED", "The derivative could not be encoded.");

        var buffer = new MemoryStream(checked((int)encoded.Size));
        encoded.SaveTo(buffer);
        buffer.Position = 0;

        var sha = Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
        buffer.Position = 0;

        logger.LogInformation(
            "event=derivative_encoded kind={DerivativeKind} width={Width} height={Height} bytes={ByteSize}",
            kind, targetWidth, targetHeight, buffer.Length);

        return new GeneratedDerivative(
            kind, buffer, "image/webp", "webp", targetWidth, targetHeight, buffer.Length, sha);
    }

    private void EnsureWithinPolicy(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new PermanentMediaException("MEDIA_INVALID_DIMENSIONS", "The image reports no usable dimensions.");
        }

        if (width > _policy.MaxDimension || height > _policy.MaxDimension)
        {
            throw new PermanentMediaException(
                "MEDIA_DIMENSIONS_EXCEEDED",
                $"Images may be at most {_policy.MaxDimension}px on either edge.");
        }

        if ((long)width * height > _policy.MaxPixels)
        {
            throw new PermanentMediaException(
                "MEDIA_PIXEL_BUDGET_EXCEEDED",
                $"Images may contain at most {_policy.MaxPixels:N0} pixels.");
        }
    }

    /// <summary>Largest power-of-two-ish scale the codec will honour without going below the target.</summary>
    private static float ComputeScale(int width, int height, int longEdge)
    {
        var currentLongEdge = Math.Max(width, height);
        if (currentLongEdge <= longEdge)
        {
            return 1f;
        }

        return Math.Min(1f, (float)longEdge / currentLongEdge);
    }

    private static (int Width, int Height) FitWithin(int width, int height, int longEdge)
    {
        var currentLongEdge = Math.Max(width, height);
        if (currentLongEdge <= longEdge)
        {
            return (width, height);
        }

        var ratio = (double)longEdge / currentLongEdge;
        return (Math.Max(1, (int)Math.Round(width * ratio)), Math.Max(1, (int)Math.Round(height * ratio)));
    }
}
