using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using PHANTOM.Application.Abstractions;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Identity;
using PHANTOM.Domain.Incidents;
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
    ILogger<DemoDataSeeder> logger,
    IIncidentNumberGenerator? numbers = null)
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
        created += await SeedRespondersAsync(cancellationToken);
        return created + await SeedIncidentsAsync(cancellationToken);
    }

    /// <summary>A demo incident: where, what, and how far a coordinator has taken it.</summary>
    public sealed record DemoIncident(
        string Title, string Description, IncidentCategory Category, string LocationText, decimal Latitude, decimal Longitude,
        double HoursAgo, Severity? VerifiedAs = null, string? Resolution = null);

    /// <summary>Fictional incidents around Lagos (Requirement 20.1), each titled "Demo:" as well as flagged IsDemoData.</summary>
    public static readonly IReadOnlyList<DemoIncident> DemoIncidents =
    [
        new("Demo: Smoke from a market stall", "Thick smoke rising from a fabric stall near the east gate. Traders are moving goods away.",
            IncidentCategory.Fire, "Balogun Market, east gate, Lagos Island", 6.45410m, 3.38870m, HoursAgo: 0.5),
        new("Demo: Flooded underpass", "Water about knee-deep in the underpass after heavy rain; two cars have stalled.",
            IncidentCategory.Flood, "Ojuelegba underpass, Surulere", 6.50950m, 3.37110m, HoursAgo: 1.5),
        new("Demo: Two-car collision on the bridge", "Two cars collided in the outbound lane; traffic is backing up. Drivers are out of their cars.",
            IncidentCategory.RoadAccident, "Third Mainland Bridge, outbound, near Oworonshoki", 6.49800m, 3.39900m, HoursAgo: 2.5),
        new("Demo: Person collapsed at a bus stop", "A man collapsed at the bus stop and is not responding; a passer-by is with him.",
            IncidentCategory.MedicalEmergency, "Ikeja Along bus stop, Ikeja", 6.60180m, 3.35150m, HoursAgo: 4, VerifiedAs: Severity.High),
        new("Demo: Fallen power line across the road", "A power line is down across the road after a truck hit the pole; no sparks seen.",
            IncidentCategory.Infrastructure, "Admiralty Way, Lekki Phase 1", 6.44740m, 3.47290m, HoursAgo: 9, VerifiedAs: Severity.Medium,
            Resolution: "Utility crew isolated the line and removed it; road reopened."),
    ];

    /// <summary>
    /// Seeds the demo incidents once, through the same domain rules as real reports: each has its original report
    /// and a queued AI analysis (processed by whichever provider is configured, labelled if it is a fixture), and
    /// its history is audited, attributed to the Demo Coordinator and marked as seeded.
    /// </summary>
    private async Task<int> SeedIncidentsAsync(CancellationToken cancellationToken)
    {
        if (numbers is null || await db.Incidents.AnyAsync(i => i.IsDemoData, cancellationToken))
        {
            return 0;
        }

        var coordinatorEmail = DemoAccounts.Single(a => a.RoleId == Role.IncidentCoordinatorId).Email;
        var coordinatorId = await db.Users.Where(u => u.Email == coordinatorEmail).Select(u => u.Id).SingleAsync(cancellationToken);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        void Audit(DateTimeOffset at, AuditActorType actor, Guid? actorId, string action, string entityType, Guid entityId, object? before, object after, object? details = null) =>
            db.AuditLogs.Add(AuditLog.Create(at, actor, actorId, action, entityType, entityId.ToString(),
                before is null ? null : JsonSerializer.Serialize(before, json), JsonSerializer.Serialize(after, json),
                JsonSerializer.Serialize(new { demoSeed = true, details }, json), null));

        var now = clock.UtcNow;
        foreach (var demo in DemoIncidents.OrderByDescending(d => d.HoursAgo))
        {
            var at = now.AddHours(-demo.HoursAgo);
            at = at.AddTicks(-(at.Ticks % TimeSpan.TicksPerMicrosecond));
            var reference = await numbers.NextAsync(cancellationToken);
            var location = Location.Reported(demo.LocationText, demo.Latitude, demo.Longitude).Value;
            var incident = Incident.Create(Guid.CreateVersion7(), reference, demo.Title, demo.Description, demo.Category, location, at, isDemoData: true).Value;
            var report = IncidentReport.Create(Guid.CreateVersion7(), reference, incident.Id, demo.Title, demo.Description, demo.Category,
                demo.LocationText, demo.Latitude, demo.Longitude, null, null, null, at, null, null, isDemoData: true);
            var analysis = IncidentAnalysis.CreatePending(Guid.CreateVersion7(), incident.Id, report.Id, attempt: 1, at);
            db.Incidents.Add(incident);
            db.IncidentReports.Add(report);
            db.IncidentAnalyses.Add(analysis);
            Audit(at, AuditActorType.Anonymous, null, AuditActions.ReportSubmitted, nameof(IncidentReport), report.Id,
                null, new { reference, category = demo.Category.ToString(), attachments = 0, hasCoordinates = true });
            Audit(at, AuditActorType.Anonymous, null, AuditActions.IncidentCreated, nameof(Incident), incident.Id,
                null, new { number = reference, status = incident.Status.ToString(), category = demo.Category.ToString() });
            Audit(at, AuditActorType.System, null, AuditActions.AiAnalysisQueued, nameof(IncidentAnalysis), analysis.Id,
                null, new { incidentId = incident.Id, attempt = 1 });

            if (demo.VerifiedAs is { } severity)
            {
                var reviewed = at.AddMinutes(6);
                incident.BeginReview(coordinatorId, reviewed);
                Audit(reviewed, AuditActorType.User, coordinatorId, AuditActions.IncidentStatusChanged, nameof(Incident), incident.Id,
                    new { status = nameof(IncidentStatus.Reported) }, new { status = nameof(IncidentStatus.UnderReview) });
                var verified = at.AddMinutes(14);
                incident.Verify(severity, coordinatorId, verified);
                Audit(verified, AuditActorType.User, coordinatorId, AuditActions.IncidentStatusChanged, nameof(Incident), incident.Id,
                    new { status = nameof(IncidentStatus.UnderReview) }, new { status = nameof(IncidentStatus.Verified) }, new { approvedSeverity = severity.ToString() });

                if (demo.Resolution is { } resolution)
                {
                    var resolved = at.AddHours(2.5);
                    incident.Resolve(resolution, coordinatorId, resolved);
                    Audit(resolved, AuditActorType.User, coordinatorId, AuditActions.IncidentStatusChanged, nameof(Incident), incident.Id,
                        new { status = nameof(IncidentStatus.Verified) }, new { status = nameof(IncidentStatus.Resolved) }, new { resolutionSummary = resolution });
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Seeded {Count} demo incidents", DemoIncidents.Count);
        return DemoIncidents.Count;
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
