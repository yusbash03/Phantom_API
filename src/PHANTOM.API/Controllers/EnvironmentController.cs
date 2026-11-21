using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PHANTOM.API.Infrastructure;
using PHANTOM.Application.Weather;

namespace PHANTOM.API.Controllers;

/// <summary>Weather context for an incident (Requirement 9). Context only: never evidence or prediction.</summary>
[ApiController]
[Route("api/v1/incidents/{incidentId:guid}/environment")]
[Produces("application/json")]
[Authorize(Policy = Policies.Staff)]
public sealed class EnvironmentController(IEnvironmentService environment) : ControllerBase
{
    /// <summary>The latest reading with its source and retrieval time, or why there is none.</summary>
    [HttpGet]
    [ProducesResponseType<EnvironmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EnvironmentResponse>> Get(Guid incidentId, CancellationToken cancellationToken) =>
        this.ToActionResult(await environment.GetAsync(incidentId, cancellationToken));

    /// <summary>Retrieve again now. Readings are cached per area for a short period, so this may return the cached one.</summary>
    [HttpPost("refresh")]
    [ProducesResponseType<EnvironmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EnvironmentResponse>> Refresh(Guid incidentId, CancellationToken cancellationToken) =>
        this.ToActionResult(await environment.RefreshAsync(incidentId, cancellationToken));
}
