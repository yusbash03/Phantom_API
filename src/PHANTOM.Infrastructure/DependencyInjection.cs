using PHANTOM.Application.Weather;
using PHANTOM.Infrastructure.Weather;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PHANTOM.Infrastructure.Realtime;
using PHANTOM.Application.Realtime;
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
        services.AddDbContext<PhantomDbContext>((sp, options) =>
        {
            var connectionString = configuration.GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"ConnectionStrings:{ConnectionStringName} is not configured. Set it with `dotnet user-secrets` or the ConnectionStrings__{ConnectionStringName} environment variable.");
            }

            ConfigureDbContext(options, connectionString);
            options.AddInterceptors(sp.GetRequiredService<RealtimeChangeInterceptor>());
        });
        // Real-time events are derived from saved changes and published after commit (design §9).
        services.TryAddSingleton<IRealtimePublisher, NullRealtimePublisher>();
        services.AddSingleton<RealtimeChangeInterceptor>();
        services.AddScoped<IPhantomDbContext>(sp => sp.GetRequiredService<PhantomDbContext>());
        services.AddScoped<IExclusiveSection, PostgresExclusiveSection>();

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => System.Text.Encoding.UTF8.GetByteCount(o.SigningKey) >= 32, "Jwt:SigningKey must be at least 32 bytes.")
            .ValidateOnStart();
        services.AddOptions<SeedOptions>().Bind(configuration.GetSection(SeedOptions.SectionName));
        services.AddOptions<LocalStorageOptions>().Bind(configuration.GetSection(LocalStorageOptions.SectionName));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IClientIpHasher, Identity.HmacClientIpHasher>();
        services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<IFileInspector, MagicByteFileInspector>();
        services.AddSingleton<IImageProcessor, SkiaSharpImageProcessor>();
        AddStorage(services, configuration, environment);
        services.AddScoped<IIncidentNumberGenerator, PostgresIncidentNumberGenerator>();
        services.AddScoped<DemoDataSeeder>();
        AddIntelligence(services, configuration, environment);
        AddGeocoding(services, configuration);
        AddWeather(services, configuration, environment);

        services.AddHealthChecks().AddDbContextCheck<PhantomDbContext>("database", tags: ["ready"]);
        return services;
    }

    internal static void ConfigureDbContext(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(WithoutGssProbe(connectionString), npgsql => npgsql
                .MigrationsHistoryTable("__ef_migrations_history")
                .EnableRetryOnFailure(3))
            .UseSnakeCaseNamingConvention();

    /// <summary>
    /// Recorded, labelled fixtures may stand in for live providers in test environments and in an explicit demo
    /// deployment (Requirement 20.3: a "Demo" environment, or Production with both Seed:Enabled and
    /// Seed:AllowDemoInProduction for judging). Never in an ordinary production deployment.
    /// </summary>
    internal static bool AllowsRecordedFixtures(IHostEnvironment environment, IConfiguration configuration) =>
        environment.IsDevelopment() || environment.IsEnvironment("Testing") || environment.IsEnvironment("Demo")
        || (configuration.GetValue<bool>("Seed:Enabled") && configuration.GetValue<bool>("Seed:AllowDemoInProduction"));

    /// <summary>
    /// Npgsql tries Kerberos (GSS) encryption first by default, which logs a native-library error in the slim
    /// container image. Neon and the other targets use TLS, so skip the probe unless a connection string asks for it.
    /// </summary>
    private static string WithoutGssProbe(string connectionString)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        if (!connectionString.Contains("GSS", StringComparison.OrdinalIgnoreCase))
        {
            builder.GssEncryptionMode = Npgsql.GssEncryptionMode.Disable;
        }

        return builder.ConnectionString;
    }

    private static void AddIntelligence(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<AiOptions>()
            .Bind(configuration.GetSection(AiOptions.SectionName))
            .Validate(o => o.Provider is AiOptions.GeminiProvider or AiOptions.RecordedFixtureProvider,
                $"Ai:Provider must be '{AiOptions.GeminiProvider}' or '{AiOptions.RecordedFixtureProvider}'.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Model), "Ai:Model is required.")
            .Validate(o => o.TimeoutSeconds is >= 1 and <= 120, "Ai:TimeoutSeconds must be 1-120.")
            .Validate(o => o.MaxRetries is >= 0 and <= 5, "Ai:MaxRetries must be 0-5.")
            .Validate(o => o.PollIntervalSeconds is >= 1 and <= 300, "Ai:PollIntervalSeconds must be 1-300.")
            .Validate(o => o.StuckAfterMinutes is >= 1 and <= 60, "Ai:StuckAfterMinutes must be 1-60.")
            .ValidateOnStart();

        var ai = configuration.GetSection(AiOptions.SectionName).Get<AiOptions>() ?? new AiOptions();
        if (ai.Provider == AiOptions.RecordedFixtureProvider)
        {
            // A replayed recording must never stand in for real analysis where real incidents are handled.
            if (!AllowsRecordedFixtures(environment, configuration))
            {
                throw new InvalidOperationException(
                    $"Ai:Provider '{AiOptions.RecordedFixtureProvider}' is for test environments and explicit demo deployments only, not '{environment.EnvironmentName}'.");
            }

            services.AddSingleton<IIncidentIntelligenceService, RecordedFixtureIntelligenceService>();
        }
        else if (ai.HasApiKey)
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

        // Transcription sends the caller's voice to the provider, so it runs only with the real provider configured.
        if (ai.Provider == AiOptions.GeminiProvider && ai.HasApiKey && ai.TranscriptionEnabled)
        {
            services.AddHttpClient<ITranscriptionService, GeminiTranscriptionService>((sp, http) =>
            {
                http.BaseAddress = sp.GetRequiredService<IOptions<AiOptions>>().Value.BaseUrl;
                http.Timeout = Timeout.InfiniteTimeSpan; // the service enforces its own timeout
            });
        }
        else
        {
            services.AddSingleton<ITranscriptionService, UnconfiguredTranscriptionService>();
        }

        services.AddSingleton<AnalysisSignal>();
        services.AddSingleton<IAnalysisSignal>(sp => sp.GetRequiredService<AnalysisSignal>());
        if (ai.WorkerEnabled)
        {
            services.AddHostedService<AnalysisWorker>();
        }
    }

    /// <summary>
    /// Design §10: local files in development, S3-compatible object storage (R2) elsewhere. Render's disk is wiped on
    /// every deploy, so Production refuses local storage rather than silently losing uploads.
    /// </summary>
    private static void AddStorage(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var provider = configuration["Storage:Provider"] ?? "Local";
        if (string.Equals(provider, "S3", StringComparison.OrdinalIgnoreCase))
        {
            services.AddOptions<S3StorageOptions>().Bind(configuration.GetSection(S3StorageOptions.SectionName))
                .Validate(o => o.IsConfigured, "Storage:S3 needs Bucket, AccessKeyId and SecretAccessKey.")
                .ValidateOnStart();
            services.AddSingleton<Amazon.S3.IAmazonS3>(sp => S3CompatibleObjectStorage.CreateClient(sp.GetRequiredService<IOptions<S3StorageOptions>>().Value));
            services.AddScoped<IObjectStorage, S3CompatibleObjectStorage>();
            return;
        }

        if (!string.Equals(provider, "Local", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Storage:Provider must be 'Local' or 'S3', not '{provider}'.");
        }

        if (environment.IsProduction())
        {
            throw new InvalidOperationException("Storage:Provider must be 'S3' in Production: the container's disk does not survive a deploy.");
        }

        services.AddScoped<IObjectStorage, LocalFileObjectStorage>();
    }

    private static void AddWeather(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<WeatherOptions>()
            .Bind(configuration.GetSection(WeatherOptions.SectionName))
            .Validate(o => o.Provider is WeatherOptions.OpenMeteoProvider or WeatherOptions.FixtureProvider,
                $"Weather:Provider must be '{WeatherOptions.OpenMeteoProvider}' or '{WeatherOptions.FixtureProvider}'.")
            .Validate(o => o.TimeoutSeconds is >= 1 and <= 30, "Weather:TimeoutSeconds must be 1-30.")
            .Validate(o => o.CacheMinutes is >= 0 and <= 1440, "Weather:CacheMinutes must be 0-1440.")
            .Validate(o => o.PollIntervalSeconds is >= 1 and <= 3600, "Weather:PollIntervalSeconds must be 1-3600.")
            .Validate(o => o.RetryFailedAfterMinutes is >= 1 and <= 1440, "Weather:RetryFailedAfterMinutes must be 1-1440.")
            .ValidateOnStart();
        services.AddMemoryCache();

        var weather = configuration.GetSection(WeatherOptions.SectionName).Get<WeatherOptions>() ?? new WeatherOptions();
        if (weather.Provider == WeatherOptions.FixtureProvider)
        {
            // A recorded reading must never be shown as current weather where real incidents are handled.
            if (!AllowsRecordedFixtures(environment, configuration))
            {
                throw new InvalidOperationException(
                    $"Weather:Provider '{WeatherOptions.FixtureProvider}' is for test environments and explicit demo deployments only, not '{environment.EnvironmentName}'.");
            }

            services.AddSingleton<IWeatherService, FixtureWeatherService>();
        }
        else
        {
            services.AddHttpClient<IWeatherService, OpenMeteoWeatherService>((sp, http) =>
                {
                    http.BaseAddress = sp.GetRequiredService<IOptions<WeatherOptions>>().Value.BaseUrl;
                    http.Timeout = Timeout.InfiniteTimeSpan; // per-call timeout is enforced by the service
                })
                // The request URL carries an incident's (rounded) coordinates; keep them out of the logs.
                .RemoveAllLoggers();
        }

        if (weather.WorkerEnabled)
        {
            services.AddHostedService<WeatherWorker>();
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
