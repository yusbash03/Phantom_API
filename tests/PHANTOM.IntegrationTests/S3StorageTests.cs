using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PHANTOM.Infrastructure.Storage;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace PHANTOM.IntegrationTests;

/// <summary>
/// Production object storage (design §10) against an S3-compatible server (Adobe S3Mock), as used with R2.
/// S3Mock does not enforce credentials or ACLs; privacy rests on the code never setting an ACL or public URL and
/// on R2 buckets being private unless public access is switched on (docs/deployment.md).
/// </summary>
public sealed class S3StorageTests : IAsyncLifetime
{
    private const string AccessKey = "phantomtest";
    private const string SecretKey = "phantom-test-secret-key";

    // MinIO no longer publishes community images on Docker Hub; S3Mock is a maintained S3 API implementation.
    private readonly IContainer _minio = new ContainerBuilder("adobe/s3mock:4.7.0")
        .WithPortBinding(9090, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9090).ForPath("/favicon.ico").ForStatusCodeMatching(_ => true)))
        .Build();
    private S3StorageOptions _options = null!;
    private IAmazonS3 _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _minio.StartAsync();
        _options = new S3StorageOptions
        {
            ServiceUrl = $"http://{_minio.Hostname}:{_minio.GetMappedPublicPort(9090)}",
            Bucket = "phantom-test",
            Region = "us-east-1",
            AccessKeyId = AccessKey,
            SecretAccessKey = SecretKey,
        };
        _client = S3CompatibleObjectStorage.CreateClient(_options);
        await _client.PutBucketAsync(_options.Bucket);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _minio.DisposeAsync();
    }

    private S3CompatibleObjectStorage Storage() => new(_client, Options.Create(_options), NullLogger<S3CompatibleObjectStorage>.Instance);

    [Fact]
    public async Task Stores_retrieves_and_deletes_an_object_byte_for_byte()
    {
        var key = Guid.NewGuid().ToString("N");
        var bytes = new byte[300_000];
        Random.Shared.NextBytes(bytes);
        var storage = Storage();

        await storage.StoreAsync(key, new MemoryStream(bytes), "audio/webm", Ct);
        await using (var read = await storage.RetrieveAsync(key, Ct))
        {
            using var copy = new MemoryStream();
            await read.CopyToAsync(copy, Ct);
            Assert.Equal(bytes, copy.ToArray());
        }

        var head = await _client.GetObjectMetadataAsync(_options.Bucket, $"attachments/{key}", Ct);
        Assert.Equal("audio/webm", head.Headers.ContentType);

        await storage.DeleteAsync(key, Ct);
        await Assert.ThrowsAsync<FileNotFoundException>(() => storage.RetrieveAsync(key, Ct));
    }

    [Fact]
    public async Task Missing_objects_are_reported_as_not_found_like_local_storage()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => Storage().RetrieveAsync(Guid.NewGuid().ToString("N"), Ct));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("attachments/abc")]
    [InlineData("")]
    public async Task Only_server_generated_keys_are_accepted(string key)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Storage().StoreAsync(key, new MemoryStream([1]), "image/jpeg", Ct));
    }
}
