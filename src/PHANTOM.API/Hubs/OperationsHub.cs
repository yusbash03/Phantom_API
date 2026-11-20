using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PHANTOM.Application.Abstractions;
using PHANTOM.Domain.Identity;
using PHANTOM.Infrastructure.Identity;

namespace PHANTOM.API.Hubs;

/// <summary>
/// Server-to-client change notifications (Requirement 13; design §9). Clients cannot call anything here.
/// Group membership comes only from the authenticated user's claims and records (Requirement 2.7):
/// staff → <c>staff</c>; a responder → <c>responder:{id}</c> of their own linked responder; everyone →
/// <c>user:{id}</c> for their own notifications.
/// </summary>
[Authorize]
public sealed class OperationsHub(IPhantomDbContext db) : Hub
{
    public const string Path = "/hubs/operations";
    public const string EventMethod = "event";
    public const string StaffGroup = "staff";

    public static string ResponderGroup(Guid responderId) => $"responder:{responderId}";

    public static string UserGroup(Guid userId) => $"user:{userId}";

    public override async Task OnConnectedAsync()
    {
        var user = Context.User;
        if (user is null || !Guid.TryParse(user.FindFirst(PhantomClaimTypes.Subject)?.Value, out var userId))
        {
            Context.Abort();
            return;
        }

        var aborted = Context.ConnectionAborted;
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId), aborted);
        if (user.IsInRole(RoleNames.Administrator) || user.IsInRole(RoleNames.IncidentCoordinator))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, StaffGroup, aborted);
        }

        if (user.IsInRole(RoleNames.Responder))
        {
            var responderIds = await db.Responders.AsNoTracking().Where(r => r.UserId == userId && r.IsActive).Select(r => r.Id).ToListAsync(aborted);
            foreach (var responderId in responderIds)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, ResponderGroup(responderId), aborted);
            }
        }

        await base.OnConnectedAsync();
    }
}
