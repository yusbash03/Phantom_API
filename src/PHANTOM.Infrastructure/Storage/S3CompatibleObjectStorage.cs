using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PHANTOM.Application.Abstractions;

namespace PHANTOM.Infrastructure.Storage;

/// <summary>Production storage (design §10): any S3-compatible service, e.g. Cloudflare R2, AWS S3 or MinIO.</summary>
public sealed class S3StorageOptions
{
    public const string SectionName = "Storage:S3";

    /// <summary>For R2: <c>https://&lt;account-id&gt;.r2.cloudflarestorage.com</c>. Empty for AWS S3 (uses <see cref="Region"/>).</summary>
    public string? ServiceUrl { get; set; }

    public string Bucket { get; set; } = string.Empty;

    /// <summary>R2 uses "auto".</summary>
    public string Region { get; set; } = "auto";

    /// <summary>Secret. From <c>Storage__S3__AccessKeyId</c>; never in source.</summary>
    public string? AccessKeyId { get; set; }

    /// <summary>Secret. From <c>Storage__S3__SecretAccessKey</c>; never in source.</summary>
    public string? SecretAccessKey { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Bucket) && !string.IsNullOrWhiteSpace(AccessKeyId) && !string.IsNullOrWhiteSpace(SecretAccessKey);
}

/// <summary>
/// Objects are private (no public ACLs or URLs); downloads always go through the authorised API. Keys are the same
/// server-generated hex ids as local storage and are validated the same way.
/// </summary>
internal sealed partial class S3CompatibleObjectStorage(IAmazonS3 s3, IOptions<S3StorageOptions> options, ILogger<S3CompatibleObjectStorage> logger) : IObjectStorage
{
    private readonly string _bucket = options.Value.Bucket;

    public static IAmazonS3 CreateClient(S3StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var config = new AmazonS3Config
        {
            // R2 and MinIO address buckets by path; R2 also needs "auto" and no default trailing checksums.
            ForcePathStyle = true,
            RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = Amazon.Runtime.ResponseChecksumValidation.WHEN_REQUIRED,
            Timeout = TimeSpan.FromSeconds(30),
        };
        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.ServiceURL = options.ServiceUrl;
            config.AuthenticationRegion = options.Region;
        }
        else
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.Region);
        }

        return new AmazonS3Client(options.AccessKeyId, options.SecretAccessKey, config);
    }

    public async Task StoreAsync(string storageKey, Stream data, string contentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        // The SDK needs a length; uploads are at most 10 MB and already buffered by intake.
        var body = data.CanSeek ? data : await CopyAsync(data, cancellationToken).ConfigureAwait(false);
        body.Position = 0;
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = Key(storageKey),
            InputStream = body,
            ContentType = contentType,
            AutoCloseStream = false,
        }, cancellationToken).ConfigureAwait(false);
        LogStored(logger, storageKey);
    }

    public async Task<Stream> RetrieveAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await s3.GetObjectAsync(_bucket, Key(storageKey), cancellationToken).ConfigureAwait(false);
            // Copy so the HTTP connection is released before the caller streams the file to a client.
            return await CopyAsync(response.ResponseStream, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"Storage object {storageKey} not found.");
        }
    }

    public async Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) =>
        await s3.DeleteObjectAsync(_bucket, Key(storageKey), cancellationToken).ConfigureAwait(false);

    private static string Key(string storageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        if (storageKey.Length > 64 || !storageKey.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("Invalid storage key.", nameof(storageKey));
        }

        return $"attachments/{storageKey}";
    }

    private static async Task<MemoryStream> CopyAsync(Stream source, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        return buffer;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Stored object {StorageKey} in S3-compatible storage")]
    private static partial void LogStored(ILogger logger, string storageKey);
}
