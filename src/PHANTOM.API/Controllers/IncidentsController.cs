using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PHANTOM.API.Infrastructure;
using PHANTOM.Application.Common;
using PHANTOM.Application.Incidents;

namespace PHANTOM.API.Controllers;

/// <summary>
/// Incident records for operational staff. Reads are open to Administrators and Coordinators;
/// every change is a Coordinator decision and carries the version the caller last read.
/// </summary>
[ApiController]
[Route("api/v1/incidents")]
[Authorize(Policy = Policies.Staff)]
[Produces("application/json")]
public sealed class IncidentsController(IIncidentService incidents) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<IncidentListItem>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<IncidentListItem>>> List([FromQuery] IncidentListQuery query, CancellationToken cancellationToken) =>
        this.ToActionResult(await incidents.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<IncidentDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IncidentDetailResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        this.ToActionResult(await incidents.GetAsync(id, cancellationToken));

    /// <summary>Edits the working title, description and category. The original public reports are never changed.</summary>
    [HttpPatch("{id:guid}")]
    [Authorize(Policy = Policies.Coordinator)]
    [ProducesResponseType<IncidentDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IncidentDetailResponse>> Edit(Guid id, [FromBody] EditIncidentRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await incidents.EditAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/review")]
    [Authorize(Policy = Policies.Coordinator)]
    [ProducesResponseType<IncidentDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IncidentDetailResponse>> BeginReview(Guid id, [FromBody] VersionedRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await incidents.BeginReviewAsync(id, request, cancellationToken));

    /// <summary>Verifies the incident. An approved severity is required and is recorded as the coordinator's decision.</summary>
    [HttpPost("{id:guid}/verify")]
    [Authorize(Policy = Policies.Coordinator)]
    [ProducesResponseType<IncidentDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IncidentDetailResponse>> Verify(Guid id, [FromBody] VerifyIncidentRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await incidents.VerifyAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = Policies.Coordinator)]
    [ProducesResponseType<IncidentDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IncidentDetailResponse>> Reject(Guid id, [FromBody] ReasonRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await incidents.RejectAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/resolve")]
    [Authorize(Policy = Policies.Coordinator)]
    [ProducesResponseType<IncidentDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IncidentDetailResponse>> Resolve(Guid id, [FromBody] ResolveIncidentRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await incidents.ResolveAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/reopen")]
    [Authorize(Policy = Policies.Coordinator)]
    [ProducesResponseType<IncidentDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IncidentDetailResponse>> Reopen(Guid id, [FromBody] ReasonRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await incidents.ReopenAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/close")]
    [Authorize(Policy = Policies.Coordinator)]
    [ProducesResponseType<IncidentDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IncidentDetailResponse>> Close(Guid id, [FromBody] VersionedRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await incidents.CloseAsync(id, request, cancellationToken));

    /// <summary>Changes the approved severity of a verified incident. AI recommendations never use this path.</summary>
    [HttpPut("{id:guid}/severity")]
    [Authorize(Policy = Policies.Coordinator)]
    [ProducesResponseType<IncidentDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IncidentDetailResponse>> ChangeSeverity(Guid id, [FromBody] ChangeSeverityRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await incidents.ChangeSeverityAsync(id, request, cancellationToken));

}
