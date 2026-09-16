using MediaPipeline.Domain.Assets;

namespace MediaPipeline.Application.Abstractions;

/// <summary>Testable clock. Used by domain services; audit timestamps are captured at transaction time.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Result of streaming an object out of storage.</summary>
public sealed record StoredObject(Stream Content, string ContentType, long ByteSize);

/// <summary>Metadata about an object, without transferring its bytes.</summary>
public sealed record ObjectMetadata(string Key, long ByteSize, DateTimeOffset LastModified);

/// <summary>
/// Object storage port. The local filesystem provider backs development and tests; Azure
/// Blob Storage backs production. Both stream - no implementation may buffer a whole file.
/// </summary>
public interface IObjectStorage
{
    /// <summary>Creates an immutable quarantine source or overwrites a deterministic derivative.</summary>
    Task WriteAsync(string key, Stream content, string contentType, CancellationToken cancellationToken);

    Task<StoredObject?> ReadAsync(string key, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>Enumerates keys under a prefix. Used only by the reconciliation tooling.</summary>
    IAsyncEnumerable<ObjectMetadata> ListAsync(string prefix, CancellationToken cancellationToken);
}

/// <summary>Password hashing port, so the algorithm can be swapped without touching identity logic.</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    /// <summary>Returns whether the password matched and whether the stored hash should be upgraded.</summary>
    (bool Verified, bool NeedsRehash) Verify(string hash, string password);
}

/// <summary>Publishes messages to the broker. Implemented by the RabbitMQ publisher.</summary>
public interface IMessagePublisher
{
    Task PublishAsync(
        string exchange,
        string routingKey,
        Guid messageId,
        string messageType,
        int contractVersion,
        ReadOnlyMemory<byte> body,
        string? traceParent,
        IReadOnlyDictionary<string, object?>? extraHeaders,
        CancellationToken cancellationToken);
}

/// <summary>Image analysis and derivative generation. The only place a decoder is touched.</summary>
public interface IMediaProcessor
{
    /// <summary>Reads intrinsic dimensions from the header without decoding the full image.</summary>
    MediaProbe Probe(Stream source);

    /// <summary>Produces one derivative. The caller owns the returned stream.</summary>
    Task<GeneratedDerivative> CreateDerivativeAsync(
        Stream source,
        DerivativeKind kind,
        CancellationToken cancellationToken);
}

public sealed record MediaProbe(string Format, int Width, int Height);

public sealed record GeneratedDerivative(
    DerivativeKind Kind,
    Stream Content,
    string ContentType,
    string Extension,
    int Width,
    int Height,
    long ByteSize,
    string Sha256) : IDisposable
{
    public void Dispose() => Content.Dispose();
}
