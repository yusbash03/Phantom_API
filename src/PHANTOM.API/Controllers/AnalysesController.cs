using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PHANTOM.API.Infrastructure;
using PHANTOM.Application.Intelligence;

namespace PHANTOM.API.Controllers;

/// <summary>AI analysis attempts for an incident. Output is advisory and never changes approved values.</summary>
[ApiController]
[Route("api/v1/incidents/{incidentId:guid}/analyses")]
[Authorize(Policy = Policies.Staff)]
[Produces("application/json")]
public sealed class AnalysesController(IAnalysisService analyses) : ControllerBase
{
    /// <summary>All attempts, newest first.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<AnalysisResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<AnalysisResponse>>> List(Guid incidentId, CancellationToken cancellationToken) =>
        this.ToActionResult(await analyses.ListAsync(incidentId, cancellationToken));

    /// <summary>Queues a new attempt when the latest one failed (Requirement 6.5).</summary>
    [HttpPost("retry")]
    [ProducesResponseType<AnalysisResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AnalysisResponse>> Retry(Guid incidentId, CancellationToken cancellationToken)
    {
        var result = await analyses.RetryAsync(incidentId, cancellationToken);
        return result.IsSuccess ? Accepted(result.Value) : this.Problem(result.Error!);
    }
}
