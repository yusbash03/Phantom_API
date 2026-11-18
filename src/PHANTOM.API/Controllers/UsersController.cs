using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PHANTOM.API.Infrastructure;
using PHANTOM.Application.Common;
using PHANTOM.Application.Users;

namespace PHANTOM.API.Controllers;

[ApiController]
[Route("api/v1/users")]
[Authorize(Policy = Policies.Administrator)]
[Produces("application/json")]
public sealed class UsersController(IUserService users) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<UserResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<UserResponse>>> List([FromQuery] UserListQuery query, CancellationToken cancellationToken) =>
        Ok(await users.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        this.ToActionResult(await users.GetAsync(id, cancellationToken));

    [HttpPost]
    [ProducesResponseType<UserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserResponse>> Create([FromBody] CreateUserRequest request, CancellationToken cancellationToken)
    {
        var result = await users.CreateAsync(request, cancellationToken);
        return result.IsSuccess
            ? CreatedAtAction(nameof(Get), new { id = result.Value.Id }, result.Value)
            : this.Problem(result.Error!);
    }

    [HttpPut("{id:guid}/roles")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserResponse>> SetRoles(Guid id, [FromBody] UpdateUserRolesRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await users.SetRolesAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/deactivate")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserResponse>> Deactivate(Guid id, CancellationToken cancellationToken) =>
        this.ToActionResult(await users.DeactivateAsync(id, cancellationToken));

    [HttpPost("{id:guid}/activate")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserResponse>> Activate(Guid id, CancellationToken cancellationToken) =>
        this.ToActionResult(await users.ActivateAsync(id, cancellationToken));
}
