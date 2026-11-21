using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PHANTOM.Application.Abstractions;
using PHANTOM.Application.Geocoding;
using PHANTOM.Application.Intelligence;
using PHANTOM.Application.Weather;
using PHANTOM.Domain.Identity;
using PHANTOM.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace PHANTOM.IntegrationTests.Infrastructure;

/// <summary>Runs the real API against a disposable PostgreSQL 17 container (requires Docker).</summary>
public sealed class PhantomApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Password = "Integration-Test-Password-1";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string ConnectionString => _postgres.GetConnectionString();

    /// <summary>Isolated object-storage directory so tests can assert exactly which files were written.</summary>
    public ScriptedIntelligenceService Intelligence { get; } = new();

    public ScriptedGeocodingService Geocoding { get; } = new();

    public ScriptedWeatherService Weather { get; } = new();

    public string StoragePath { get; } = Path.Combine(Path.GetTempPath(), $"phantom-it-{Guid.NewGuid():N}");

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        _ = Server; // builds the host, which applies migrations
    }

    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        if (Directory.Exists(StoragePath))
        {
            Directory.Delete(StoragePath, recursive: true);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Phantom"] = _postgres.GetConnectionString(),
            ["Jwt:SigningKey"] = "integration-test-signing-key-at-least-32-bytes-long",
            ["Database:MigrateOnStartup"] = "true",
            ["Seed:Enabled"] = "false",
            ["RateLimiting:LoginPermitsPerMinute"] = "10000",
            ["RateLimiting:PublicPermitsPerMinute"] = "10000",
            ["Storage:Local:BasePath"] = StoragePath,
            ["Ai:PollIntervalSeconds"] = "1",
            ["Mapbox:PollIntervalSeconds"] = "1",
            ["Weather:PollIntervalSeconds"] = "1",
        }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IIncidentIntelligenceService>();
            services.AddSingleton<IIncidentIntelligenceService>(Intelligence);
            services.RemoveAll<IGeocodingService>();
            services.AddSingleton<IGeocodingService>(Geocoding);
            services.RemoveAll<IWeatherService>();
            services.AddSingleton<IWeatherService>(Weather);
        });
    }

    /// <summary>Creates an active user with the given roles directly in the database.</summary>
    public async Task<User> CreateUserAsync(params Guid[] roleIds)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var user = User.Create(Guid.CreateVersion7(), $"user-{Guid.NewGuid():N}@test.local", "Test User", DateTimeOffset.UtcNow);
        user.SetPasswordHash(hasher.Hash(user, Password));
        user.SetRoles(roleIds);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public async Task<LoginResult> LoginAsync(string email, string password = Password)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        string? cookie = null;
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            cookie = values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith("phantom_refresh=", StringComparison.Ordinal));
        }

        string? token = null;
        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<TokenBody>();
            token = body!.AccessToken;
        }

        return new LoginResult(response, token, cookie);
    }

    public async Task<HttpClient> CreateClientForRoleAsync(params Guid[] roleIds)
    {
        var user = await CreateUserAsync(roleIds);
        var login = await LoginAsync(user.Email);
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return client;
    }

    public sealed record LoginResult(HttpResponseMessage Response, string? AccessToken, string? RefreshCookie);

    private sealed record TokenBody(string AccessToken);
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<PhantomApiFactory>
{
    public const string Name = "api";
}
