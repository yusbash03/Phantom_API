using Microsoft.EntityFrameworkCore;
using PHANTOM.Domain.Assignments;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Correlation;
using PHANTOM.Domain.Identity;
using PHANTOM.Domain.Incidents;
using PHANTOM.Domain.Notifications;
using PHANTOM.Domain.Responders;

namespace PHANTOM.Application.Abstractions;

/// <summary>Unit of work over the PHANTOM database. One <c>SaveChangesAsync</c> = one transaction.</summary>
public interface IPhantomDbContext
{
    DbSet<User> Users { get; }
    DbSet<Role> Roles { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<Incident> Incidents { get; }
    DbSet<IncidentReport> IncidentReports { get; }
    DbSet<Attachment> Attachments { get; }
    DbSet<IncidentAnalysis> IncidentAnalyses { get; }
    DbSet<IncidentRelation> IncidentRelations { get; }
    DbSet<Responder> Responders { get; }
    DbSet<ResponderAssignment> ResponderAssignments { get; }
    DbSet<IncidentUpdate> IncidentUpdates { get; }
    DbSet<Notification> Notifications { get; }
    DbSet<PHANTOM.Domain.Weather.EnvironmentalSnapshot> EnvironmentalSnapshots { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
