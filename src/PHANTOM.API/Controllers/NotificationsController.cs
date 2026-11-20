using Microsoft.AspNetCore.Mvc;
using PHANTOM.API.Infrastructure;
using PHANTOM.Application.Notifications;

namespace PHANTOM.API.Controllers;

/// <summary>
/// The signed-in user's own in-app notifications (Requirement 14). Any authenticated user; the service
/// only ever reads or changes the caller's own notifications.
/// </summary>
[ApiController]
[Route("api/v1/notifications")]
[Produces("application/json")]
public sealed class NotificationsController(INotificationService notifications) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<NotificationListResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<NotificationListResponse>> List([FromQuery] NotificationListQuery query, CancellationToken cancellationToken) =>
        Ok(await notifications.ListAsync(query, cancellationToken));

    [HttpPost("{id:guid}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken cancellationToken)
    {
        var result = await notifications.MarkReadAsync(id, cancellationToken);
        return result.IsSuccess ? NoContent() : this.Problem(result.Error!);
    }

    [HttpPost("read-all")]
    [ProducesResponseType<MarkAllReadResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<MarkAllReadResponse>> MarkAllRead(CancellationToken cancellationToken) =>
        Ok(await notifications.MarkAllReadAsync(cancellationToken));
}
