using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PHANTOM.API.Infrastructure;
using PHANTOM.Application.Settings;

namespace PHANTOM.API.Controllers;

/// <summary>
/// Operational settings (Requirement 17), administrators only. Only the listed operational values exist here;
/// secrets and connection settings are environment configuration and are never exposed (17.3).
/// </summary>
[ApiController]
[Route("api/v1/settings")]
[Produces("application/json")]
[Authorize(Policy = Policies.Administrator)]
public sealed class SettingsController(ISettingsService settings) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<SettingsResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SettingsResponse>> Get(CancellationToken cancellationToken) => Ok(await settings.GetAsync(cancellationToken));

    /// <summary>Changes take effect within 30 seconds and are audited with the values before and after.</summary>
    [HttpPut]
    [ProducesResponseType<SettingsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SettingsResponse>> Update(UpdateSettingsRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await settings.UpdateAsync(request, cancellationToken));
}
