using System.Runtime.CompilerServices;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MediaPipeline.Application.Abstractions;
using MediaPipeline.Domain.Assets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaPipeline.Infrastructure.Storage;

public sealed class AzureBlobStorageOptions
{
    public const string SectionName = "Storage:AzureBlob";

    /// <summary>
    /// Blob service endpoint, e.g. <c>https://collaborative-media-pipelineprod.blob.core.windows.net</c>.
    /// Preferred over a connection string so production never handles an account key.
    /// </summary>
    public string? ServiceUri { get; set; }

    /// <summary>
    /// Escape hatch for local emulator use only. Production must leave this unset and
    /// authenticate with a managed identity instead.
    /// </summary>
    public string? ConnectionString { get; set; }

    public string ContainerName { get; set; } = "assets";

    /// <summary>
    /// Creates the container on first use. For the local emulator only: in Azure the
    /// container is created by Terraform and the application's managed identity holds
    /// data-plane access alone, so it could not create one even if asked to.
    /// </summary>
    public bool CreateContainerIfMissing { get; set; }
}

/// <summary>
/// Production object storage. Containers are private; nothing is ever served directly
/// from the storage account, so no public URL or long-lived SAS is issued.
/// </summary>
public sealed class AzureBlobObjectStorage : IObjectStorage
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<AzureBlobObjectStorage> _logger;

    public AzureBlobObjectStorage(IOptions<AzureBlobStorageOptions> options, ILogger<AzureBlobObjectStorage> logger)
    {
        _logger = logger;
        var value = options.Value;

        if (!string.IsNullOrWhiteSpace(value.ConnectionString))
        {
            // Emulator/dev path only.
            _container = new BlobServiceClient(value.ConnectionString).GetBlobContainerClient(value.ContainerName);
        }
        else if (!string.IsNullOrWhiteSpace(value.ServiceUri))
        {
            // Managed identity in Azure; developer credentials locally. No secret material.
            var credential = new DefaultAzureCredential();
            _container = new BlobServiceClient(new Uri(value.ServiceUri), credential)
                .GetBlobContainerClient(value.ContainerName);
        }
        else
        {
            throw new InvalidOperationException(
                "Azure Blob storage requires either Storage:AzureBlob:ServiceUri or a ConnectionString.");
        }

        if (value.CreateContainerIfMissing)
        {
            _container.CreateIfNotExists();
            _logger.LogInformation("event=blob_container_ensured container={ContainerName}", value.ContainerName);
        }
    }

    public async Task WriteAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        StorageKeys.EnsureSafe(key);
        var blob = _container.GetBlobClient(key);

        await blob.UploadAsync(
            content,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
                Conditions = key.StartsWith("quarantine/", StringComparison.Ordinal) ? new BlobRequestConditions { IfNoneMatch = ETag.All } : null,
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredObject?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        StorageKeys.EnsureSafe(key);
        var blob = _container.GetBlobClient(key);

        try
        {
            var response = await blob.DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var details = response.Value.Details;
            return new StoredObject(
                response.Value.Content,
                details.ContentType ?? "application/octet-stream",
                details.ContentLength);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        StorageKeys.EnsureSafe(key);
        var response = await _container.GetBlobClient(key).ExistsAsync(cancellationToken).ConfigureAwait(false);
        return response.Value;
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        StorageKeys.EnsureSafe(key);
        var response = await _container.GetBlobClient(key)
            .DeleteIfExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        if (response.Value)
        {
            _logger.LogInformation("event=object_deleted key={ObjectKey}", key);
        }

        return response.Value;
    }

    public async IAsyncEnumerable<ObjectMetadata> ListAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _container.GetBlobsAsync(
            BlobTraits.None, BlobStates.None, prefix, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return new ObjectMetadata(
                item.Name,
                item.Properties.ContentLength ?? 0,
                item.Properties.LastModified ?? DateTimeOffset.MinValue);
        }
    }
}
