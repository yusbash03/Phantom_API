using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PHANTOM.Infrastructure.Storage;

namespace PHANTOM.IntegrationTests;

/// <summary>
/// Opt-in check of the production storage settings against the real provider (e.g. Supabase Storage). Runs only when
/// PHANTOM_LIVE_S3_SERVICE_URL, _REGION, _BUCKET, _ACCESS_KEY_ID and _SECRET_ACCESS_KEY are set; credentials are never
/// stored in the repository. Writes one small object and deletes it again.
/// <c>dotnet test --project tests/PHANTOM.IntegrationTests --filter-trait "Category=Live"</c>
/// </summary>
[Trait("Category", "Live")]
public sealed class S3LiveTests
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable($"PHANTOM_LIVE_S3_{name}");

    [Fact]
    public async Task Configured_bucket_stores_reads_and_deletes_privately()
    {
        var options = new S3StorageOptions
        {
            ServiceUrl = Env("SERVICE_URL"),
            Region = Env("REGION") ?? "auto",
            Bucket = Env("BUCKET") ?? string.Empty,
            AccessKeyId = Env("ACCESS_KEY_ID"),
            SecretAccessKey = Env("SECRET_ACCESS_KEY"),
        };
        Assert.SkipWhen(!options.IsConfigured || string.IsNullOrWhiteSpace(options.ServiceUrl), "Set PHANTOM_LIVE_S3_* to run the live storage check.");

        var ct = TestContext.Current.CancellationToken;
        using var client = S3CompatibleObjectStorage.CreateClient(options);
        var storage = new S3CompatibleObjectStorage(client, Options.Create(options), NullLogger<S3CompatibleObjectStorage>.Instance);
        var key = Guid.NewGuid().ToString("N");
        var bytes = new byte[64 * 1024];
        Random.Shared.NextBytes(bytes);

        await storage.StoreAsync(key, new MemoryStream(bytes), "application/octet-stream", ct);
        try
        {
            await using (var read = await storage.RetrieveAsync(key, ct))
            {
                using var copy = new MemoryStream();
                await read.CopyToAsync(copy, ct);
                Assert.Equal(bytes, copy.ToArray());
            }

            // Supabase serves public buckets at /storage/v1/object/public/<bucket>/<path>; this bucket must not be public.
            var endpoint = new Uri(options.ServiceUrl!);
            if (endpoint.Host.EndsWith(".supabase.co", StringComparison.OrdinalIgnoreCase))
            {
                var projectHost = endpoint.Host.Replace(".storage.supabase.co", ".supabase.co", StringComparison.OrdinalIgnoreCase);
                using var anonymous = new HttpClient();
                var response = await anonymous.GetAsync(new Uri($"https://{projectHost}/storage/v1/object/public/{options.Bucket}/attachments/{key}"), ct);
                Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            }
        }
        finally
        {
            await storage.DeleteAsync(key, ct);
        }

        await Assert.ThrowsAsync<FileNotFoundException>(() => storage.RetrieveAsync(key, ct));
    }
}
