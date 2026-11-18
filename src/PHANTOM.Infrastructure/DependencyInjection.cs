using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PHANTOM.Application.Abstractions;
using PHANTOM.Infrastructure.Common;
using PHANTOM.Infrastructure.Identity;
using PHANTOM.Infrastructure.Persistence;
using PHANTOM.Infrastructure.Seeding;

namespace PHANTOM.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Phantom";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
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

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddScoped<DemoDataSeeder>();

        services.AddHealthChecks().AddDbContextCheck<PhantomDbContext>("database", tags: ["ready"]);
        return services;
    }

    internal static void ConfigureDbContext(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql
                .MigrationsHistoryTable("__ef_migrations_history")
                .EnableRetryOnFailure(3))
            .UseSnakeCaseNamingConvention();
}
