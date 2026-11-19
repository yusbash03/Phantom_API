using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PHANTOM.API.Infrastructure;
using PHANTOM.Application.Reporting.Intake;
using PHANTOM.Domain.Incidents;

namespace PHANTOM.API.Controllers;

/// <summary>Anonymous endpoints for public incident reporting. No internal data is exposed.</summary>
[ApiController]
[Route("api/v1/public/reports")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.Public)]
[Produces("application/json")]
public sealed class PublicReportsController(IReportIntakeService intake) : ControllerBase
{
    // Multipart framing overhead on top of the largest permitted payload.
    private const long MaxRequestBytes = (ReportIntakeService.MaxFiles * ReportIntakeService.MaxFileSizeBytes) + (1024 * 1024);

    /// <summary>Submits a public incident report and returns its reference.</summary>
    /// <remarks>
    /// Send an <c>Idempotency-Key</c> (UUID) header; repeating it within 24 hours returns the original
    /// reference instead of creating a second report.
    /// </remarks>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    [ProducesResponseType<SubmitReportResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<SubmitReportResponse>> Submit(
        [FromForm] PublicReportForm form,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        var files = form.Files?.Select(f => new FileAttachment(f.FileName, f.OpenReadStream(), f.ContentType)).ToList() ?? [];
        try
        {
            var result = await intake.SubmitAsync(new SubmitReportRequest
            {
                Title = form.Title,
                Description = form.Description,
                Category = form.Category,
                LocationText = form.LocationText,
                Latitude = form.Latitude,
                Longitude = form.Longitude,
                ReporterName = form.ReporterName,
                ReporterPhone = form.ReporterPhone,
                ReporterEmail = form.ReporterEmail,
                IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim(),
                ClientIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Files = files,
            }, cancellationToken);

            return result.IsSuccess
                ? CreatedAtAction(nameof(GetStatus), new { reference = result.Value.Reference }, result.Value)
                : this.Problem(result.Error!);
        }
        finally
        {
            foreach (var file in files)
            {
                await file.Stream.DisposeAsync();
            }
        }
    }

    /// <summary>Returns the coarse public status of a submitted report.</summary>
    [HttpGet("{reference}/status")]
    [ProducesResponseType<PublicStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PublicStatusResponse>> GetStatus(string reference, CancellationToken cancellationToken) =>
        this.ToActionResult(await intake.GetPublicStatusAsync(reference, cancellationToken));
}

/// <summary>Multipart form for a public report. Field names are the camelCase property names.</summary>
public sealed class PublicReportForm
{
    public required string Title { get; init; }

    public required string Description { get; init; }

    public required IncidentCategory Category { get; init; }

    public required string LocationText { get; init; }

    public decimal? Latitude { get; init; }

    public decimal? Longitude { get; init; }

    public string? ReporterName { get; init; }

    public string? ReporterPhone { get; init; }

    public string? ReporterEmail { get; init; }

    /// <summary>Up to 5 images (JPEG, PNG, WebP) and 1 audio recording (WebM, OGG, MP3, WAV, M4A), 10 MB each.</summary>
    public IFormFileCollection? Files { get; init; }
}
