using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PHANTOM.Application.Abstractions;
using PHANTOM.Domain.Identity;
using PHANTOM.Domain.Responders;
using PHANTOM.Infrastructure.Persistence;

namespace PHANTOM.Infrastructure.Seeding;

/// <summary>Seeds clearly-labelled demonstration accounts and responders (Requirement 20.1). Idempotent.</summary>
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

    /// <summary>Fictional responders. The first is linked to the demo Responder account so its self-service works.</summary>
    public static readonly IReadOnlyList<(string CallSign, string Name, string Team, string[] Skills, ResponderAvailability Availability)> DemoResponders =
    [
        ("MED-01", "Amina Bello (demo)", "Medical Response", ["first aid", "paramedic"], ResponderAvailability.Available),
        ("FIRE-02", "Tunde Okafor (demo)", "Fire & Rescue", ["firefighting", "rescue", "first aid"], ResponderAvailability.Available),
        ("SEC-03", "Ngozi Adeyemi (demo)", "Security Patrol", ["security", "crowd control"], ResponderAvailability.Available),
        ("RD-04", "Kelechi Musa (demo)", "Road Safety", ["traffic control", "first aid"], ResponderAvailability.Available),
        ("FLD-05", "Halima Yusuf (demo)", "Flood Response", ["water rescue", "evacuation"], ResponderAvailability.Unavailable),
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
        return created + await SeedRespondersAsync(cancellationToken);
    }

    private async Task<int> SeedRespondersAsync(CancellationToken cancellationToken)
    {
        var responderEmail = DemoAccounts.Single(a => a.RoleId == Role.ResponderId).Email;
        var responderUserId = await db.Users.Where(u => u.Email == responderEmail).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(cancellationToken);
        var linkedAlready = responderUserId is not null && await db.Responders.AnyAsync(r => r.UserId == responderUserId, cancellationToken);
        var created = 0;
        for (var i = 0; i < DemoResponders.Count; i++)
        {
            var (callSign, name, team, skills, availability) = DemoResponders[i];
            if (await db.Responders.AnyAsync(r => r.CallSign == callSign, cancellationToken))
            {
                continue;
            }

            var linkUser = i == 0 && !linkedAlready ? responderUserId : null;
            var now = clock.UtcNow;
            var responder = Responder.Create(Guid.CreateVersion7(), callSign, name, team, "+234 800 000 000" + i, null, skills, linkUser, now, isDemoData: true).Value;
            responder.SetAvailability(availability, hasActiveAssignment: false, now);
            db.Responders.Add(responder);
            created++;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Seeded {Count} demo responders", created);
        return created;
    }
}
