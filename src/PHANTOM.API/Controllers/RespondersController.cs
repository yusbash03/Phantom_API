using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PHANTOM.API.Infrastructure;
using PHANTOM.Application.Common;
using PHANTOM.Application.Responders;

namespace PHANTOM.API.Controllers;

/// <summary>
/// Responder profiles (Requirement 10). Staff read; Administrators create and change them; a
/// responder may set only their own availability.
/// </summary>
[ApiController]
[Route("api/v1/responders")]
[Produces("application/json")]
public sealed class RespondersController(IResponderService responders) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = Policies.Staff)]
    [ProducesResponseType<PagedResult<ResponderResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<ResponderResponse>>> List([FromQuery] ResponderListQuery query, CancellationToken cancellationToken) =>
        Ok(await responders.ListAsync(query, cancellationToken));

    [HttpGet("facets")]
    [Authorize(Policy = Policies.Staff)]
    [ProducesResponseType<ResponderFacets>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ResponderFacets>> Facets(CancellationToken cancellationToken) =>
        Ok(await responders.FacetsAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Policies.Staff)]
    [ProducesResponseType<ResponderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ResponderResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        this.ToActionResult(await responders.GetAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = Policies.Administrator)]
    [ProducesResponseType<ResponderResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ResponderResponse>> Create([FromBody] CreateResponderRequest request, CancellationToken cancellationToken)
    {
        var result = await responders.CreateAsync(request, cancellationToken);
        return result.IsSuccess ? CreatedAtAction(nameof(Get), new { id = result.Value.Id }, result.Value) : this.Problem(result.Error!);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.Administrator)]
    [ProducesResponseType<ResponderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ResponderResponse>> Update(Guid id, [FromBody] UpdateResponderRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await responders.UpdateAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = Policies.Administrator)]
    [ProducesResponseType<ResponderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ResponderResponse>> Deactivate(Guid id, [FromBody] ResponderVersionRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await responders.DeactivateAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/activate")]
    [Authorize(Policy = Policies.Administrator)]
    [ProducesResponseType<ResponderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ResponderResponse>> Activate(Guid id, [FromBody] ResponderVersionRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await responders.ActivateAsync(id, request, cancellationToken));

    [HttpPut("{id:guid}/availability")]
    [Authorize(Policy = Policies.Administrator)]
    [ProducesResponseType<ResponderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ResponderResponse>> SetAvailability(Guid id, [FromBody] SetAvailabilityRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await responders.SetAvailabilityAsync(id, request, cancellationToken));

    /// <summary>The signed-in responder's own profile.</summary>
    [HttpGet("me")]
    [Authorize(Policy = Policies.Responder)]
    [ProducesResponseType<ResponderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ResponderResponse>> Mine(CancellationToken cancellationToken) =>
        this.ToActionResult(await responders.GetMineAsync(cancellationToken));

    /// <summary>The signed-in responder sets their own availability (Available or Unavailable).</summary>
    [HttpPut("me/availability")]
    [Authorize(Policy = Policies.Responder)]
    [ProducesResponseType<ResponderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ResponderResponse>> SetMyAvailability([FromBody] SetAvailabilityRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await responders.SetMyAvailabilityAsync(request, cancellationToken));
}
