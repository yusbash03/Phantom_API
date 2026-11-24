using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PHANTOM.Application.Abstractions;
using PHANTOM.Infrastructure.Persistence;
using PHANTOM.Infrastructure.Seeding;
using PHANTOM.IntegrationTests.Infrastructure;

namespace PHANTOM.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class SeedingTests(PhantomApiFactory factory)
{
    [Theory]
    [InlineData("Production", true, false, false)]
    [InlineData("Production", true, true, true)]
    [InlineData("Development", true, false, true)]
    [InlineData("Development", false, false, false)]
    public async Task Demo_seeding_respects_environment_guard(string environment, bool enabled, bool allowInProduction, bool expected)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var seeder = new DemoDataSeeder(
            scope.ServiceProvider.GetRequiredService<PhantomDbContext>(),
            scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
            scope.ServiceProvider.GetRequiredService<IClock>(),
            new StubEnvironment(environment),
            Options.Create(new SeedOptions { Enabled = enabled, AllowDemoInProduction = allowInProduction, DemoUserPassword = "Demo-Password-123" }),
            NullLogger<DemoDataSeeder>.Instance);

        Assert.Equal(expected, seeder.IsAllowed());
        if (!expected)
        {
            Assert.Equal(0, await seeder.SeedAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Demo_incidents_are_seeded_once_labelled_and_through_the_normal_pipeline()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        DemoDataSeeder Seeder() => new(
            db,
            scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
            scope.ServiceProvider.GetRequiredService<IClock>(),
            new StubEnvironment("Development"),
            Options.Create(new SeedOptions { Enabled = true, DemoUserPassword = "Demo-Password-123" }),
            NullLogger<DemoDataSeeder>.Instance,
            scope.ServiceProvider.GetRequiredService<IIncidentNumberGenerator>());

        await Seeder().SeedAsync(ct);
        Assert.Equal(0, await Seeder().SeedAsync(ct)); // idempotent

        var incidents = await db.Incidents.AsNoTracking().Where(i => i.IsDemoData).ToListAsync(ct);
        Assert.Equal(DemoDataSeeder.DemoIncidents.Count, incidents.Count);
        Assert.All(incidents, i => Assert.StartsWith("Demo: ", i.Title, StringComparison.Ordinal));
        Assert.Equal(3, incidents.Count(i => i.Status == Domain.Incidents.IncidentStatus.Reported));
        Assert.Single(incidents, i => i.Status == Domain.Incidents.IncidentStatus.Verified && i.ApprovedSeverity == Domain.Incidents.Severity.High);
        var resolved = Assert.Single(incidents, i => i.Status == Domain.Incidents.IncidentStatus.Resolved);
        Assert.True(resolved.ResolvedAt > resolved.CreatedAt);

        var ids = incidents.Select(i => i.Id).ToList();
        Assert.Equal(incidents.Count, await db.IncidentReports.CountAsync(r => ids.Contains(r.IncidentId) && r.IsDemoData, ct));
        Assert.Equal(incidents.Count, await db.IncidentAnalyses.CountAsync(a => ids.Contains(a.IncidentId), ct));

        // Their history is audited, attributed to the Demo Coordinator, and marked as seeded.
        var coordinator = await db.Users.SingleAsync(u => u.Email == "coordinator@phantom.demo", ct);
        var verifyAudit = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == Domain.Audit.AuditActions.IncidentStatusChanged && a.ActorUserId == coordinator.Id && a.EntityId == resolved.Id.ToString())
            .ToListAsync(ct);
        Assert.Equal(3, verifyAudit.Count);
        Assert.All(verifyAudit, a => Assert.Contains("demoSeed", a.Metadata, StringComparison.Ordinal));
    }

    private sealed class StubEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "PHANTOM.API";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
