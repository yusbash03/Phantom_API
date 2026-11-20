using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PHANTOM.Application.Abstractions;
using PHANTOM.Application.Geocoding;
using PHANTOM.Application.Intelligence;
using PHANTOM.Infrastructure.Common;
using PHANTOM.Infrastructure.Geocoding;
using PHANTOM.Infrastructure.Identity;
using PHANTOM.Infrastructure.Intelligence;
using PHANTOM.Infrastructure.Persistence;
using PHANTOM.Infrastructure.Seeding;
using PHANTOM.Infrastructure.Storage;
using PHANTOM.Infrastructure.Workers;

namespace PHANTOM.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Phantom";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        // Resolved lazily so tooling (e.g. build-time OpenAPI export) can build the host without a database.
        services.AddDbContext<PhantomDbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"ConnectionStrings:{ConnectionStringName} is not configured. Set it with `dotnet user-secrets` or the ConnectionStrings__{ConnectionStringName} environment variable.");
            }

            ConfigureDbContext(options, connectionString);
        });
        services.AddScoped<IPhantomDbContext>(sp => sp.GetRequiredService<PhantomDbContext>());

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => System.Text.Encoding.UTF8.GetByteCount(o.SigningKey) >= 32, "Jwt:SigningKey must be at least 32 bytes.")
            .ValidateOnStart();
        services.AddOptions<SeedOptions>().Bind(configuration.GetSection(SeedOptions.SectionName));
        services.AddOptions<LocalStorageOptions>().Bind(configuration.GetSection(LocalStorageOptions.SectionName));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<IFileInspector, MagicByteFileInspector>();
        services.AddSingleton<IImageProcessor, SkiaSharpImageProcessor>();
        services.AddScoped<IObjectStorage, LocalFileObjectStorage>();
        services.AddScoped<IIncidentNumberGenerator, PostgresIncidentNumberGenerator>();
        services.AddScoped<DemoDataSeeder>();
        AddIntelligence(services, configuration, environment);
        AddGeocoding(services, configuration);

        services.AddHealthChecks().AddDbContextCheck<PhantomDbContext>("database", tags: ["ready"]);
        return services;
    }

    internal static void ConfigureDbContext(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql
                .MigrationsHistoryTable("__ef_migrations_history")
                .EnableRetryOnFailure(3))
            .UseSnakeCaseNamingConvention();

    private static void AddIntelligence(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<AiOptions>()
            .Bind(configuration.GetSection(AiOptions.SectionName))
            .Validate(o => o.Provider == AiOptions.GeminiProvider, $"Ai:Provider must be '{AiOptions.GeminiProvider}'.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Model), "Ai:Model is required.")
            .Validate(o => o.TimeoutSeconds is >= 1 and <= 120, "Ai:TimeoutSeconds must be 1-120.")
            .Validate(o => o.MaxRetries is >= 0 and <= 5, "Ai:MaxRetries must be 0-5.")
            .Validate(o => o.PollIntervalSeconds is >= 1 and <= 300, "Ai:PollIntervalSeconds must be 1-300.")
            .Validate(o => o.StuckAfterMinutes is >= 1 and <= 60, "Ai:StuckAfterMinutes must be 1-60.")
            .ValidateOnStart();

        var ai = configuration.GetSection(AiOptions.SectionName).Get<AiOptions>() ?? new AiOptions();
        if (ai.HasApiKey)
        {
            services.AddHttpClient<IIncidentIntelligenceService, GeminiIntelligenceService>((sp, http) =>
            {
                http.BaseAddress = sp.GetRequiredService<IOptions<AiOptions>>().Value.BaseUrl;
                http.Timeout = Timeout.InfiniteTimeSpan; // per-attempt timeout is enforced by the service
            });
        }
        else if (environment.IsDevelopment())
        {
            // Development convenience only; output is labelled "Simulated" everywhere.
            services.AddSingleton<IIncidentIntelligenceService, SimulatedIntelligenceService>();
        }
        else
        {
            services.AddSingleton<IIncidentIntelligenceService, UnconfiguredIntelligenceService>();
        }

        services.AddSingleton<AnalysisSignal>();
        services.AddSingleton<IAnalysisSignal>(sp => sp.GetRequiredService<AnalysisSignal>());
        if (ai.WorkerEnabled)
        {
            services.AddHostedService<AnalysisWorker>();
        }
    }

    private static void AddGeocoding(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MapboxOptions>()
            .Bind(configuration.GetSection(MapboxOptions.SectionName))
            .Validate(o => o.TimeoutSeconds is >= 1 and <= 60, "Mapbox:TimeoutSeconds must be 1-60.")
            .Validate(o => o.PollIntervalSeconds is >= 1 and <= 300, "Mapbox:PollIntervalSeconds must be 1-300.")
            .Validate(o => o.StuckAfterMinutes is >= 1 and <= 60, "Mapbox:StuckAfterMinutes must be 1-60.")
            .ValidateOnStart();

        var mapbox = configuration.GetSection(MapboxOptions.SectionName).Get<MapboxOptions>() ?? new MapboxOptions();
        if (mapbox.HasToken)
        {
            services.AddHttpClient<IGeocodingService, MapboxGeocodingService>((sp, http) =>
                {
                    http.BaseAddress = sp.GetRequiredService<IOptions<MapboxOptions>>().Value.BaseUrl;
                    http.Timeout = Timeout.InfiniteTimeSpan; // per-call timeout is enforced by the service
                })
                // Mapbox takes the token in the query string; the default HttpClient logs would record it.
                .RemoveAllLoggers();
        }
        else
        {
            // No simulated geocoder: coordinates are never invented (Requirement 8.4).
            services.AddSingleton<IGeocodingService, UnconfiguredGeocodingService>();
        }

        services.AddSingleton<GeocodingSignal>();
        services.AddSingleton<IGeocodingSignal>(sp => sp.GetRequiredService<GeocodingSignal>());
        if (mapbox.WorkerEnabled)
        {
            services.AddHostedService<GeocodingWorker>();
        }
    }
}
