using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PHANTOM.API.Infrastructure;
using PHANTOM.Application.Audit;
using PHANTOM.Application.Dashboard;

namespace PHANTOM.API.Controllers;

/// <summary>The command dashboard (Requirement 12). Metrics are counted from the database on every request.</summary>
[ApiController]
[Route("api/v1/dashboard")]
[Produces("application/json")]
[Authorize(Policy = Policies.Staff)]
public sealed class DashboardController(IDashboardService dashboard, IAuditQueryService audit) : ControllerBase
{
    /// <summary>Headline counts: total, active, high priority, awaiting review, unassigned, unacknowledged, available responders (12.1).</summary>
    [HttpGet("summary")]
    [ProducesResponseType<DashboardSummary>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DashboardSummary>> Summary(CancellationToken cancellationToken) =>
        Ok(await dashboard.SummaryAsync(cancellationToken));

    /// <summary>The most recent incident activity from the audit log, newest first (12.3).</summary>
    [HttpGet("activity")]
    [ProducesResponseType<IReadOnlyList<ActivityEvent>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ActivityEvent>>> Activity([FromQuery] int limit = 20, CancellationToken cancellationToken = default) =>
        Ok(await audit.RecentActivityAsync(limit, cancellationToken));
}
