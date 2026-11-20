using Microsoft.AspNetCore.SignalR;
using PHANTOM.Application.Realtime;

namespace PHANTOM.API.Hubs;

/// <summary>
/// Routes each committed change to the groups allowed to see it (Requirement 13.4). Staff see all operational
/// events; a responder sees events about their own assignments and availability; notifications go only to
/// their recipient. Delivery is best effort: a failure is logged and never affects the request that caused it.
/// </summary>
internal sealed partial class SignalRRealtimePublisher(IHubContext<OperationsHub> hub, ILogger<SignalRRealtimePublisher> logger) : IRealtimePublisher
{
    public async Task PublishAsync(IReadOnlyList<RealtimeDelivery> deliveries, CancellationToken cancellationToken)
    {
        foreach (var delivery in deliveries)
        {
            try
            {
                var e = delivery.Event;
                if (delivery.RecipientUserId is { } recipient)
                {
                    await hub.Clients.Group(OperationsHub.UserGroup(recipient)).SendAsync(OperationsHub.EventMethod, e, cancellationToken);
                    continue;
                }

                await hub.Clients.Group(OperationsHub.StaffGroup).SendAsync(OperationsHub.EventMethod, e, cancellationToken);
                if (e.ResponderId is { } responderId)
                {
                    await hub.Clients.Group(OperationsHub.ResponderGroup(responderId)).SendAsync(OperationsHub.EventMethod, e, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPublishFailed(logger, ex, delivery.Event.Type);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Real-time event {Type} could not be delivered")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, RealtimeEventType type);
}
