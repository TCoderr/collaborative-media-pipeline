using System.Security.Cryptography;
using MediaPipeline.Application.Abstractions;

namespace MediaPipeline.Application.Media;

/// <summary>
/// Wraps an upload stream so it can be handed straight to object storage while the bytes
/// are hashed and counted in flight.
/// <para>
/// Nothing is buffered beyond the sniff window, so a 25 MB upload costs a few kilobytes of
/// memory rather than 25 MB. The size limit is enforced <em>during</em> the copy, so an
/// oversized body is cut off mid-transfer instead of being fully accepted and then rejected.
/// </para>
/// </summary>
public sealed class HashingUploadStream : Stream
{
    private readonly Stream _inner;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly long _maxBytes;
    private readonly ReadOnlyMemory<byte> _prefix;

    private int _prefixOffset;
    private long _bytesRead;
    private string? _digest;

    public HashingUploadStream(Stream inner, ReadOnlyMemory<byte> prefix, long maxBytes)
    {
        _inner = inner;
        _prefix = prefix;
        _maxBytes = maxBytes;

        // The sniff window was already consumed from the source; it still counts toward
        // the hash and the size budget.
        _hash.AppendData(prefix.Span);
        _bytesRead = prefix.Length;
        EnsureWithinLimit();
    }

    public long BytesRead => _bytesRead;

    /// <summary>Lower-case hex SHA-256 of everything read. Stable after the first call.</summary>
    public string Digest => _digest ??= Convert.ToHexStringLower(_hash.GetHashAndReset());

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _bytesRead;
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_prefixOffset < _prefix.Length)
        {
            var take = Math.Min(buffer.Length, _prefix.Length - _prefixOffset);
            _prefix.Slice(_prefixOffset, take).CopyTo(buffer);
            _prefixOffset += take;
            return take;
        }

        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            _hash.AppendData(buffer.Span[..read]);
            _bytesRead += read;
            EnsureWithinLimit();
        }

        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    private void EnsureWithinLimit()
    {
        if (_bytesRead > _maxBytes)
        {
            throw MediaPipelineException.Validation(
                "UPLOAD_TOO_LARGE", $"Uploads may be at most {_maxBytes / (1024 * 1024)} MB.");
        }
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Reads just enough of a stream to identify its media type from the leading bytes.</summary>
public static class UploadSniffer
{
    public static async Task<(AcceptedMediaType Media, ReadOnlyMemory<byte> Prefix)> SniffAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[MediaSignatures.SniffLength];
        var filled = 0;

        while (filled < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        if (filled == 0)
        {
            throw MediaPipelineException.Validation("EMPTY_UPLOAD", "The uploaded file is empty.");
        }

        var media = MediaSignatures.Detect(buffer.AsSpan(0, filled));
        return (media, buffer.AsMemory(0, filled));
    }
}
