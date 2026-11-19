using Microsoft.EntityFrameworkCore;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Identity;
using PHANTOM.Domain.Incidents;

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

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
