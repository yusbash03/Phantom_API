using Microsoft.EntityFrameworkCore;
using PHANTOM.Application.Abstractions;
using PHANTOM.Domain.Assignments;
using PHANTOM.Domain.Incidents;
using PHANTOM.Domain.Responders;

namespace PHANTOM.Application.Dashboard;

/// <summary>
/// The command dashboard's headline numbers (Requirement 12.1). Every value is counted from the database
/// when requested (12.2); nothing is cached or stored.
/// </summary>
public sealed record DashboardSummary(
    int TotalIncidents,
    int ActiveIncidents,
    int HighPriorityActiveIncidents,
    int AwaitingReview,
    int UnassignedVerified,
    int AssignmentsAwaitingAcknowledgement,
    int AvailableResponders,
    DateTimeOffset GeneratedAt);

public interface IDashboardService
{
    Task<DashboardSummary> SummaryAsync(CancellationToken cancellationToken);
}

internal sealed class DashboardService(IPhantomDbContext db, IClock clock) : IDashboardService
{
    private static readonly IncidentStatus[] Ended = [IncidentStatus.Resolved, IncidentStatus.Closed, IncidentStatus.Rejected];

    public async Task<DashboardSummary> SummaryAsync(CancellationToken cancellationToken)
    {
        // One grouped query for incidents: per status, how many and how many are approved High/Critical.
        var byStatus = await db.Incidents.AsNoTracking()
            .GroupBy(i => i.Status)
            .Select(g => new
            {
                Status = g.Key,
                Count = g.Count(),
                HighPriority = g.Count(i => i.ApprovedSeverity == Severity.High || i.ApprovedSeverity == Severity.Critical),
            })
            .ToListAsync(cancellationToken);

        var active = byStatus.Where(s => !Ended.Contains(s.Status)).ToList();
        int Count(params IncidentStatus[] statuses) => byStatus.Where(s => statuses.Contains(s.Status)).Sum(s => s.Count);

        var awaitingAcknowledgement = await db.ResponderAssignments.AsNoTracking()
            .CountAsync(a => a.Status == AssignmentStatus.Assigned, cancellationToken);
        var availableResponders = await db.Responders.AsNoTracking()
            .CountAsync(r => r.IsActive && r.Availability == ResponderAvailability.Available, cancellationToken);

        return new DashboardSummary(
            TotalIncidents: byStatus.Sum(s => s.Count),
            ActiveIncidents: active.Sum(s => s.Count),
            HighPriorityActiveIncidents: active.Sum(s => s.HighPriority),
            AwaitingReview: Count(IncidentStatus.Reported, IncidentStatus.UnderReview),
            // A verified incident moves to Assigned when its first responder is assigned, so Verified means unassigned.
            UnassignedVerified: Count(IncidentStatus.Verified),
            AssignmentsAwaitingAcknowledgement: awaitingAcknowledgement,
            AvailableResponders: availableResponders,
            GeneratedAt: clock.UtcNow);
    }
}
