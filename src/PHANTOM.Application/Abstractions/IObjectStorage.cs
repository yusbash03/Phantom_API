namespace PHANTOM.Application.Abstractions;

/// <summary>Abstraction over object storage. Keys are server-generated UUIDs — never client file names.</summary>
public interface IObjectStorage
{
    /// <summary>Stores <paramref name="data"/> under the given <paramref name="storageKey"/>.</summary>
    Task StoreAsync(string storageKey, Stream data, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Returns a stream for downloading the object, or throws if not found.</summary>
    Task<Stream> RetrieveAsync(string storageKey, CancellationToken cancellationToken = default);

    /// <summary>Deletes the object. No-op if the key does not exist.</summary>
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default);
}
