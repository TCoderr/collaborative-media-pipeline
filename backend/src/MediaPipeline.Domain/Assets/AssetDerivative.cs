namespace MediaPipeline.Domain.Assets;

/// <summary>
/// The derivative outputs the media worker produces. Kept as a small closed set so
/// storage keys stay deterministic and reprocessing is naturally idempotent.
/// </summary>
public enum DerivativeKind
{
    /// <summary>Small square-ish tile used by the spiral gallery.</summary>
    Thumbnail = 0,

    /// <summary>Larger view used on the asset detail surface.</summary>
    Preview = 1,
}

/// <summary>
/// A processed output belonging to one <see cref="AssetVersion"/>. Modelled as its own
/// row rather than columns on the version, so adding a derivative kind is additive.
/// </summary>
public sealed class AssetDerivative
{
    private AssetDerivative() { }

    public Guid Id { get; private set; }
    public Guid AssetVersionId { get; private set; }
    public DerivativeKind Kind { get; private set; }
    public string ObjectKey { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public long ByteSize { get; private set; }
    public string Sha256 { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    internal static AssetDerivative Create(
        Guid assetVersionId,
        DerivativeKind kind,
        string objectKey,
        string contentType,
        int width,
        int height,
        long byteSize,
        string sha256,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            AssetVersionId = assetVersionId,
            Kind = kind,
            ObjectKey = objectKey,
            ContentType = contentType,
            Width = width,
            Height = height,
            ByteSize = byteSize,
            Sha256 = sha256,
            CreatedAt = now,
            UpdatedAt = now,
        };

    internal void Update(
        string objectKey,
        string contentType,
        int width,
        int height,
        long byteSize,
        string sha256,
        DateTimeOffset now)
    {
        ObjectKey = objectKey;
        ContentType = contentType;
        Width = width;
        Height = height;
        ByteSize = byteSize;
        Sha256 = sha256;
        UpdatedAt = now;
    }
}
