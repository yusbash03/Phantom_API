using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PHANTOM.API.Infrastructure;
using PHANTOM.Application.Correlation;

namespace PHANTOM.API.Controllers;

/// <summary>
/// Possible duplicates and links between incidents. Suggestions are never acted on automatically;
/// every decision is a coordinator's, and nothing is deleted or hidden.
/// </summary>
[ApiController]
[Produces("application/json")]
public sealed class RelationsController(ICorrelationService correlation) : ControllerBase
{
    [HttpGet("api/v1/incidents/{incidentId:guid}/relations")]
    [Authorize(Policy = Policies.Staff)]
    [ProducesResponseType<IReadOnlyList<RelationResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<RelationResponse>>> List(Guid incidentId, CancellationToken cancellationToken) =>
        this.ToActionResult(await correlation.ListAsync(incidentId, cancellationToken));

    /// <summary>Links another incident (by number) as Related without closing either.</summary>
    [HttpPost("api/v1/incidents/{incidentId:guid}/relations")]
    [Authorize(Policy = Policies.Coordinator)]
    [ProducesResponseType<RelationResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RelationResponse>> Link(Guid incidentId, [FromBody] LinkIncidentRequest request, CancellationToken cancellationToken)
    {
        var result = await correlation.LinkAsync(incidentId, request, cancellationToken);
        return result.IsSuccess
            ? CreatedAtAction(nameof(List), new { incidentId }, result.Value)
            : this.Problem(result.Error!);
    }

    /// <summary>Confirm a duplicate (requires the primary), mark related, or mark unrelated (never re-suggested).</summary>
    [HttpPut("api/v1/relations/{id:guid}")]
    [Authorize(Policy = Policies.Coordinator)]
    [ProducesResponseType<RelationDecisionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RelationDecisionResponse>> Decide(Guid id, [FromBody] RelationDecisionRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await correlation.DecideAsync(id, request, cancellationToken));
}
