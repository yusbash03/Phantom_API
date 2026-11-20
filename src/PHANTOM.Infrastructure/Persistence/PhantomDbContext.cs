using Microsoft.EntityFrameworkCore;
using PHANTOM.Application.Abstractions;
using PHANTOM.Domain.Assignments;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Correlation;
using PHANTOM.Domain.Identity;
using PHANTOM.Domain.Incidents;
using PHANTOM.Domain.Responders;

namespace PHANTOM.Infrastructure.Persistence;

public sealed class PhantomDbContext(DbContextOptions<PhantomDbContext> options) : DbContext(options), IPhantomDbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<IncidentReport> IncidentReports => Set<IncidentReport>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<IncidentAnalysis> IncidentAnalyses => Set<IncidentAnalysis>();
    public DbSet<IncidentRelation> IncidentRelations => Set<IncidentRelation>();
    public DbSet<Responder> Responders => Set<Responder>();
    public DbSet<ResponderAssignment> ResponderAssignments => Set<ResponderAssignment>();
    public DbSet<IncidentUpdate> IncidentUpdates => Set<IncidentUpdate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PhantomDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        // Enums are stored as readable strings, never as ordinals that break when reordered.
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(40);
    }
}
