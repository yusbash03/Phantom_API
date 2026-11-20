using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PHANTOM.Application.Abstractions;
using PHANTOM.Application.Realtime;
using PHANTOM.Domain.Assignments;
using PHANTOM.Domain.Correlation;
using PHANTOM.Domain.Incidents;
using PHANTOM.Domain.Notifications;
using PHANTOM.Domain.Responders;

namespace PHANTOM.Infrastructure.Realtime;

/// <summary>
/// Derives real-time events from what EF is about to save, so no write path can forget to publish, and
/// publishes them only after the data is committed (design §9): straight after SaveChanges, or when an
/// explicit transaction commits. A failed save or a rollback publishes nothing.
/// </summary>
internal sealed class RealtimeChangeInterceptor(IRealtimePublisher publisher, IClock clock) : DbTransactionInterceptor, ISaveChangesInterceptor
{
    private sealed class State
    {
        public List<RealtimeDelivery> Staged { get; } = [];
        public List<RealtimeDelivery> AwaitingCommit { get; } = [];
    }

    private readonly ConditionalWeakTable<DbContext, State> states = new();

    // ── Save changes ──────────────────────────────────────────────────────────

    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stage(eventData.Context);
        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stage(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        AfterSave(eventData.Context).GetAwaiter().GetResult();
        return result;
    }

    public async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        await AfterSave(eventData.Context);
        return result;
    }

    public void SaveChangesFailed(DbContextErrorEventData eventData) => Discard(eventData.Context, staged: true);

    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context, staged: true);
        return Task.CompletedTask;
    }

    public void SaveChangesCanceled(DbContextEventData eventData) => Discard(eventData.Context, staged: true);

    public Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context, staged: true);
        return Task.CompletedTask;
    }

    // ── Explicit transactions ─────────────────────────────────────────────────

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        Flush(eventData.Context).GetAwaiter().GetResult();

    public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
        await Flush(eventData.Context);

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => Discard(eventData.Context, staged: false);

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context, staged: false);
        return Task.CompletedTask;
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private void Stage(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var state = states.GetOrCreateValue(context);
        state.Staged.Clear();
        state.Staged.AddRange(Collect(context.ChangeTracker, clock.UtcNow));
    }

    private async Task AfterSave(DbContext? context)
    {
        if (context is null || !states.TryGetValue(context, out var state))
        {
            return;
        }

        state.AwaitingCommit.AddRange(state.Staged);
        state.Staged.Clear();
        if (context.Database.CurrentTransaction is null)
        {
            await Flush(context);
        }
    }

    private async Task Flush(DbContext? context)
    {
        if (context is null || !states.TryGetValue(context, out var state) || state.AwaitingCommit.Count == 0)
        {
            return;
        }

        var deliveries = state.AwaitingCommit.Distinct().ToList();
        state.AwaitingCommit.Clear();
        await publisher.PublishAsync(deliveries, CancellationToken.None);
    }

    private void Discard(DbContext? context, bool staged)
    {
        if (context is not null && states.TryGetValue(context, out var state))
        {
            state.Staged.Clear();
            if (!staged)
            {
                state.AwaitingCommit.Clear();
            }
        }
    }

    internal static IEnumerable<RealtimeDelivery> Collect(ChangeTracker tracker, DateTimeOffset at)
    {
        RealtimeDelivery Event(RealtimeEventType type, Guid? incident = null, Guid? assignment = null, Guid? responder = null, Guid? recipient = null) =>
            new(new RealtimeEvent(type, incident, assignment, responder, at), recipient);

        foreach (var entry in tracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            var added = entry.State == EntityState.Added;
            switch (entry.Entity)
            {
                case Incident incident when added:
                    yield return Event(RealtimeEventType.IncidentCreated, incident.Id);
                    break;
                case Incident incident when Changed(entry, nameof(Incident.Status)):
                    yield return Event(RealtimeEventType.IncidentStatusChanged, incident.Id);
                    if (incident.Status == IncidentStatus.Resolved)
                    {
                        yield return Event(RealtimeEventType.IncidentResolved, incident.Id);
                    }

                    break;
                case Incident incident:
                    yield return Event(RealtimeEventType.IncidentUpdated, incident.Id);
                    break;
                case IncidentAnalysis analysis when Changed(entry, nameof(IncidentAnalysis.Status))
                    && analysis.Status is AnalysisStatus.Succeeded or AnalysisStatus.Failed:
                    yield return Event(RealtimeEventType.AnalysisCompleted, analysis.IncidentId);
                    break;
                case ResponderAssignment assignment when added:
                    yield return Event(RealtimeEventType.AssignmentCreated, assignment.IncidentId, assignment.Id, assignment.ResponderId);
                    break;
                case ResponderAssignment assignment when Changed(entry, nameof(ResponderAssignment.Status)):
                    yield return Event(assignment.Status == AssignmentStatus.Acknowledged ? RealtimeEventType.AssignmentAcknowledged : RealtimeEventType.AssignmentStatusChanged,
                        assignment.IncidentId, assignment.Id, assignment.ResponderId);
                    break;
                case IncidentUpdate update when added:
                    yield return Event(RealtimeEventType.AssignmentUpdated, update.IncidentId, update.AssignmentId);
                    break;
                case Responder responder when Changed(entry, nameof(Responder.Availability)) || Changed(entry, nameof(Responder.IsActive)):
                    yield return Event(RealtimeEventType.ResponderAvailabilityChanged, responder: responder.Id);
                    break;
                case IncidentRelation relation:
                    yield return Event(RealtimeEventType.IncidentUpdated, relation.SourceIncidentId);
                    yield return Event(RealtimeEventType.IncidentUpdated, relation.TargetIncidentId);
                    break;
                case IncidentReport report when Changed(entry, nameof(IncidentReport.IncidentId)):
                    yield return Event(RealtimeEventType.IncidentUpdated, report.IncidentId);
                    yield return Event(RealtimeEventType.IncidentUpdated, (Guid)entry.Property(nameof(IncidentReport.IncidentId)).OriginalValue!);
                    break;
                case Notification notification when added:
                    yield return Event(RealtimeEventType.NotificationCreated, notification.IncidentId, notification.AssignmentId, recipient: notification.RecipientUserId);
                    break;
            }
        }
    }

    private static bool Changed(EntityEntry entry, string property)
    {
        var p = entry.Property(property);
        return p.IsModified && !Equals(p.OriginalValue, p.CurrentValue);
    }
}
