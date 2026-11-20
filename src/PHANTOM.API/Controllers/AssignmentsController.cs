using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PHANTOM.API.Infrastructure;
using PHANTOM.Application.Assignments;

namespace PHANTOM.API.Controllers;

/// <summary>
/// The assignment workflow (Requirement 11). Coordinators assign and cancel; only the assigned
/// responder progresses an assignment, and anyone else receives 404.
/// </summary>
[ApiController]
[Produces("application/json")]
public sealed class AssignmentsController(IAssignmentService assignments) : ControllerBase
{
    [HttpPost("api/v1/incidents/{incidentId:guid}/assignments")]
    [Authorize(Policy = Policies.Coordinator)]
    [ProducesResponseType<AssignmentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AssignmentResponse>> Assign(Guid incidentId, [FromBody] AssignResponderRequest request, CancellationToken cancellationToken)
    {
        var result = await assignments.AssignAsync(incidentId, request, cancellationToken);
        return result.IsSuccess ? CreatedAtAction(nameof(Get), new { id = result.Value.Id }, result.Value) : this.Problem(result.Error!);
    }

    [HttpGet("api/v1/incidents/{incidentId:guid}/assignments")]
    [Authorize(Policy = Policies.Staff)]
    [ProducesResponseType<IReadOnlyList<AssignmentResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<AssignmentResponse>>> ListForIncident(Guid incidentId, CancellationToken cancellationToken) =>
        this.ToActionResult(await assignments.ListForIncidentAsync(incidentId, cancellationToken));

    [HttpPost("api/v1/assignments/{id:guid}/cancel")]
    [Authorize(Policy = Policies.Coordinator)]
    [ProducesResponseType<AssignmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AssignmentResponse>> Cancel(Guid id, [FromBody] AssignmentReasonRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await assignments.CancelAsync(id, request, cancellationToken));

    /// <summary>The signed-in responder's assignments, active first.</summary>
    [HttpGet("api/v1/assignments/mine")]
    [Authorize(Policy = Policies.Responder)]
    [ProducesResponseType<IReadOnlyList<AssignmentResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AssignmentResponse>>> Mine(CancellationToken cancellationToken) =>
        Ok(await assignments.MineAsync(cancellationToken));

    /// <summary>Staff, or the assigned responder; anyone else gets 404.</summary>
    [HttpGet("api/v1/assignments/{id:guid}")]
    [ProducesResponseType<AssignmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AssignmentResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        this.ToActionResult(await assignments.GetAsync(id, cancellationToken));

    /// <summary>Acknowledgement is acceptance (decision D6).</summary>
    [HttpPost("api/v1/assignments/{id:guid}/acknowledge")]
    [Authorize(Policy = Policies.Responder)]
    [ProducesResponseType<AssignmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AssignmentResponse>> Acknowledge(Guid id, CancellationToken cancellationToken) =>
        this.ToActionResult(await assignments.AcknowledgeAsync(id, cancellationToken));

    [HttpPost("api/v1/assignments/{id:guid}/decline")]
    [Authorize(Policy = Policies.Responder)]
    [ProducesResponseType<AssignmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AssignmentResponse>> Decline(Guid id, [FromBody] AssignmentReasonRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await assignments.DeclineAsync(id, request, cancellationToken));

    /// <summary>Reports the responder EnRoute or OnScene.</summary>
    [HttpPost("api/v1/assignments/{id:guid}/status")]
    [Authorize(Policy = Policies.Responder)]
    [ProducesResponseType<AssignmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AssignmentResponse>> SetStatus(Guid id, [FromBody] AssignmentStatusRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await assignments.SetStatusAsync(id, request, cancellationToken));

    [HttpPost("api/v1/assignments/{id:guid}/updates")]
    [Authorize(Policy = Policies.Responder)]
    [ProducesResponseType<AssignmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AssignmentResponse>> PostUpdate(Guid id, [FromBody] AssignmentUpdateRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await assignments.PostUpdateAsync(id, request, cancellationToken));

    /// <summary>Completes the assignment with resolution notes. Only a coordinator resolves the incident.</summary>
    [HttpPost("api/v1/assignments/{id:guid}/complete")]
    [Authorize(Policy = Policies.Responder)]
    [ProducesResponseType<AssignmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AssignmentResponse>> Complete(Guid id, [FromBody] CompleteAssignmentRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await assignments.CompleteAsync(id, request, cancellationToken));
}

