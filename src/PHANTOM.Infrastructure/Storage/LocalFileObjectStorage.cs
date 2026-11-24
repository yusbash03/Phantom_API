using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PHANTOM.Application.Abstractions;

namespace PHANTOM.Infrastructure.Storage;

/// <summary>Development-only file system storage. Files are written outside the web root under a configured directory.</summary>
internal sealed class LocalFileObjectStorage(IOptions<LocalStorageOptions> options, ILogger<LocalFileObjectStorage> logger) : IObjectStorage
{
    private readonly string _basePath = options.Value.BasePath;

    public async Task StoreAsync(string storageKey, Stream data, string contentType, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        ArgumentNullException.ThrowIfNull(data);

        var path = FilePath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var file = File.Create(path);
        await data.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        logger.LogDebug("Stored object {StorageKey} at {Path}", storageKey, path);
    }

    public async Task<Stream> RetrieveAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        var path = FilePath(storageKey);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Storage object {storageKey} not found.", path);
        }

        // Return a MemoryStream so we're not holding a file handle open.
        var ms = new MemoryStream();
        await using var file = File.OpenRead(path);
        await file.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        ms.Position = 0;
        return ms;
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        var path = FilePath(storageKey);
        if (File.Exists(path))
        {
            File.Delete(path);
            logger.LogDebug("Deleted object {StorageKey}", storageKey);
        }

        return Task.CompletedTask;
    }

    private string FilePath(string storageKey)
    {
        // Keys are server-generated hex UUIDs; anything else must never reach the file system.
        if (storageKey.Length > 64 || !storageKey.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("Invalid storage key.", nameof(storageKey));
        }

        return Path.Combine(_basePath, storageKey);
    }
}

public sealed class LocalStorageOptions
{
    public const string SectionName = "Storage:Local";
    public string BasePath { get; init; } = Path.Combine(Path.GetTempPath(), "phantom-storage");
}
