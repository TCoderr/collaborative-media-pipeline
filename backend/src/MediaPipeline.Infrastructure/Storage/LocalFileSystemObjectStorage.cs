using System.Runtime.CompilerServices;
using MediaPipeline.Application.Abstractions;
using MediaPipeline.Domain.Assets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaPipeline.Infrastructure.Storage;

public sealed class LocalStorageOptions
{
    public const string SectionName = "Storage:Local";

    /// <summary>Root directory. Must be outside the web root - nothing here is publicly served.</summary>
    public string RootPath { get; set; } = "./.local-storage";
}

/// <summary>
/// Filesystem-backed object storage for development and integration tests. Real I/O, real
/// streaming, real failure modes - the tests exercise the same code path production does,
/// minus the Azure SDK.
/// </summary>
public sealed class LocalFileSystemObjectStorage : IObjectStorage
{
    private readonly string _root;
    private readonly ILogger<LocalFileSystemObjectStorage> _logger;

    public LocalFileSystemObjectStorage(IOptions<LocalStorageOptions> options, ILogger<LocalFileSystemObjectStorage> logger)
    {
        _root = Path.GetFullPath(options.Value.RootPath);
        _logger = logger;
        Directory.CreateDirectory(_root);

        // A relative RootPath resolves against each process's working directory, so the
        // API and the worker can end up with different stores and every job fails with
        // SOURCE_OBJECT_MISSING. Logging the resolved absolute path makes that a
        // one-glance diagnosis instead of a hunt through the worker.
        _logger.LogInformation("event=local_storage_ready");
    }

    public async Task WriteAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        var path = ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write to a temp file then move: a crash mid-write can never leave a truncated
        // object visible under the real key.
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await using (file.ConfigureAwait(false))
            {
                await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: !key.StartsWith("quarantine/", StringComparison.Ordinal));

            // Content type is metadata the filesystem cannot hold; a sidecar keeps the
            // provider's contract identical to Blob Storage.
            await File.WriteAllTextAsync(path + ".type", contentType, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public async Task<StoredObject?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        var path = ResolvePath(key);
        if (!File.Exists(path))
        {
            return null;
        }

        var contentType = File.Exists(path + ".type")
            ? await File.ReadAllTextAsync(path + ".type", cancellationToken).ConfigureAwait(false)
            : "application/octet-stream";

        var info = new FileInfo(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return new StoredObject(stream, contentType, info.Length);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(File.Exists(ResolvePath(key)));

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var path = ResolvePath(key);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        TryDelete(path + ".type");
        _logger.LogInformation("event=object_deleted key={ObjectKey}", key);
        return Task.FromResult(true);
    }

    public async IAsyncEnumerable<ObjectMetadata> ListAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var start = string.IsNullOrEmpty(prefix) ? _root : ResolvePath(prefix.TrimEnd('/'));
        var searchRoot = Directory.Exists(start) ? start : _root;

        if (!Directory.Exists(searchRoot))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.EndsWith(".type", StringComparison.Ordinal) || file.Contains(".tmp-", StringComparison.Ordinal))
            {
                continue;
            }

            var key = Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var info = new FileInfo(file);
            yield return new ObjectMetadata(key, info.Length, info.LastWriteTimeUtc);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Validates the key, then confirms the resolved absolute path is still inside the
    /// storage root. Belt and braces: the key format check alone would suffice, but a
    /// path-traversal bug here would be a filesystem write primitive.
    /// </summary>
    private string ResolvePath(string key)
    {
        StorageKeys.EnsureSafe(key);

        var combined = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(combined, _root, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Resolved object path escaped the storage root.");
        }

        return combined;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort: a leftover temp file is collected by reconciliation.
        }
    }
}
