using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PHANTOM.Application.Abstractions;
using PHANTOM.Domain.Identity;
using PHANTOM.Infrastructure.Persistence;

namespace PHANTOM.Infrastructure.Seeding;

/// <summary>Seeds clearly-labelled demonstration accounts. Idempotent.</summary>
public sealed class DemoDataSeeder(
    PhantomDbContext db,
    IPasswordHasher passwordHasher,
    IClock clock,
    IHostEnvironment environment,
    IOptions<SeedOptions> options,
    ILogger<DemoDataSeeder> logger)
{
    public static readonly IReadOnlyList<(string Email, string DisplayName, Guid RoleId)> DemoAccounts =
    [
        ("admin@phantom.demo", "Demo Administrator", Role.AdministratorId),
        ("coordinator@phantom.demo", "Demo Coordinator", Role.IncidentCoordinatorId),
        ("responder@phantom.demo", "Demo Responder", Role.ResponderId),
    ];

    public bool IsAllowed()
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return false;
        }

        return !environment.IsProduction() || settings.AllowDemoInProduction;
    }

    public async Task<int> SeedAsync(CancellationToken cancellationToken)
    {
        if (!IsAllowed())
        {
            logger.LogInformation("Demo data seeding skipped for environment {Environment}", environment.EnvironmentName);
            return 0;
        }

        var password = options.Value.DemoUserPassword;
        if (string.IsNullOrWhiteSpace(password) || password.Length < User.MinPasswordLength)
        {
            throw new InvalidOperationException($"Seed:DemoUserPassword must be configured (min {User.MinPasswordLength} characters) when seeding is enabled.");
        }

        var created = 0;
        foreach (var (email, displayName, roleId) in DemoAccounts)
        {
            if (await db.Users.AnyAsync(u => u.Email == email, cancellationToken))
            {
                continue;
            }

            var user = User.Create(Guid.CreateVersion7(), email, displayName, clock.UtcNow, isDemoData: true);
            user.SetPasswordHash(passwordHasher.Hash(user, password));
            user.SetRoles([roleId]);
            db.Users.Add(user);
            created++;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Seeded {Count} demo accounts", created);
        return created;
    }
}
