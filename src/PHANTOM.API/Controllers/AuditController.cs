using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PHANTOM.API.Infrastructure;
using PHANTOM.Application.Audit;
using PHANTOM.Application.Common;

namespace PHANTOM.API.Controllers;

/// <summary>Read-only access to the append-only audit log (Requirement 15). There is no API to change it (15.3).</summary>
[ApiController]
[Produces("application/json")]
public sealed class AuditController(IAuditQueryService audit) : ControllerBase
{
    /// <summary>The incident's full history: reports, AI analyses, decisions, assignments and updates (15.4, 15.5).</summary>
    [HttpGet("api/v1/incidents/{incidentId:guid}/timeline")]
    [Authorize(Policy = Policies.Staff)]
    [ProducesResponseType<IReadOnlyList<TimelineEvent>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<TimelineEvent>>> Timeline(Guid incidentId, CancellationToken cancellationToken) =>
        this.ToActionResult(await audit.TimelineAsync(incidentId, cancellationToken));

    /// <summary>Search by actor, entity, action and date range (15.6).</summary>
    [HttpGet("api/v1/audit")]
    [Authorize(Policy = Policies.Administrator)]
    [ProducesResponseType<PagedResult<AuditEntryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<AuditEntryResponse>>> Search([FromQuery] AuditSearchQuery query, CancellationToken cancellationToken) =>
        this.ToActionResult(await audit.SearchAsync(query, cancellationToken));

    [HttpGet("api/v1/audit/facets")]
    [Authorize(Policy = Policies.Administrator)]
    [ProducesResponseType<AuditFacets>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuditFacets>> Facets(CancellationToken cancellationToken) =>
        Ok(await audit.FacetsAsync(cancellationToken));
}
