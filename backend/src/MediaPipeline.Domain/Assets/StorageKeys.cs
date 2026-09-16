using MediaPipeline.Domain.Common;

namespace MediaPipeline.Domain.Assets;

/// <summary>
/// The only place object-storage keys are constructed. Every segment is a server-side
/// identifier, so a client filename can never influence where bytes land.
/// </summary>
public static class StorageKeys
{
    public static string Source(Guid projectId, Guid assetId, Guid versionId) =>
        $"quarantine/{projectId:N}/{assetId:N}/{versionId:N}/source";

    public static string Derivative(Guid projectId, Guid assetId, Guid versionId, DerivativeKind kind, string extension) =>
        $"projects/{projectId:N}/assets/{assetId:N}/versions/{versionId:N}/derivatives/{kind.ToString().ToLowerInvariant()}.{extension}";

    /// <summary>
    /// Defence in depth: a key must be a plain relative path made of the characters we
    /// generate. Anything else is rejected before it reaches a storage provider.
    /// </summary>
    public static void EnsureSafe(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 512)
        {
            throw new DomainException("INVALID_OBJECT_KEY", "Object key is missing or too long.");
        }

        if (key.StartsWith('/') || key.Contains("..", StringComparison.Ordinal)
            || key.Contains('\\', StringComparison.Ordinal) || key.Contains(':', StringComparison.Ordinal))
        {
            throw new DomainException("INVALID_OBJECT_KEY", "Object key must be a plain relative path.");
        }

        foreach (var c in key)
        {
            var ok = char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.';
            if (!ok)
            {
                throw new DomainException("INVALID_OBJECT_KEY", "Object key contains an unsupported character.");
            }
        }
    }
}
