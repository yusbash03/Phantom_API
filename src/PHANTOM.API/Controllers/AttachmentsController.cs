using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using PHANTOM.API.Infrastructure;
using PHANTOM.Application.Incidents;

namespace PHANTOM.API.Controllers;

/// <summary>Authorised attachment downloads. Files are never served from a public static path.</summary>
[ApiController]
[Route("api/v1/attachments")]
[Authorize(Policy = Policies.Staff)]
public sealed class AttachmentsController(IIncidentService incidents) : ControllerBase
{
    [HttpGet("{id:guid}/content")]
    [ProducesResponseType<FileStreamResult>(StatusCodes.Status200OK, "application/octet-stream")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Content(Guid id, CancellationToken cancellationToken)
    {
        var result = await incidents.GetAttachmentContentAsync(id, cancellationToken);
        if (result.IsFailure)
        {
            return this.Problem(result.Error!);
        }

        // Always a download, never rendered inline; the sniffed content type is the one we stored.
        Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        Response.Headers[HeaderNames.CacheControl] = "private, no-store";
        var content = result.Value;
        return File(content.Content, content.ContentType, content.FileName);
    }
}
