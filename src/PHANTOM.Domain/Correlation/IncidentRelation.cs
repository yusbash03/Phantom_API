using PHANTOM.Domain.Common;

namespace PHANTOM.Domain.Correlation;

public enum RelationType
{
    PossibleDuplicate,
    ConfirmedDuplicate,
    Related,
    Unrelated,
}

/// <summary>
/// A link between two incidents. The pair is stored ordered (<see cref="SourceIncidentId"/> &lt;
/// <see cref="TargetIncidentId"/>) so each pair exists once and is symmetric. Decisions update the type
/// and are audited; a relation is never deleted (Requirement 7.4).
/// </summary>
public sealed class IncidentRelation
{
    public const int MaxExplanationLength = 1000;

    private IncidentRelation()
    {
        // EF Core
    }

    public Guid Id { get; private set; }
    public Guid SourceIncidentId { get; private set; }
    public Guid TargetIncidentId { get; private set; }
    public RelationType Type { get; private set; }

    /// <summary>Correlation score in [0, 1]; null for a manual link.</summary>
    public decimal? Score { get; private set; }

    /// <summary>Signal breakdown (JSON) behind <see cref="Score"/>.</summary>
    public string? Signals { get; private set; }

    public string? Explanation { get; private set; }

    /// <summary>Null when suggested by the system.</summary>
    public Guid? CreatedById { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? DecidedById { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>For a confirmed duplicate, the incident that stays open and receives the reports.</summary>
    public Guid? PrimaryIncidentId { get; private set; }

    public static (Guid Source, Guid Target) Order(Guid a, Guid b)
    {
        if (a == b)
        {
            throw new ArgumentException("An incident cannot be related to itself.");
        }

        return a.CompareTo(b) < 0 ? (a, b) : (b, a);
    }

    public bool Involves(Guid incidentId) => SourceIncidentId == incidentId || TargetIncidentId == incidentId;

    public Guid Other(Guid incidentId) =>
        SourceIncidentId == incidentId ? TargetIncidentId
        : TargetIncidentId == incidentId ? SourceIncidentId
        : throw new ArgumentException("The incident is not part of this relation.", nameof(incidentId));

    public static IncidentRelation Suggest(Guid id, Guid a, Guid b, decimal score, string signalsJson, string explanation, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(score, 0m);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(score, 1m);
        var (source, target) = Order(a, b);
        return new IncidentRelation
        {
            Id = id,
            SourceIncidentId = source,
            TargetIncidentId = target,
            Type = RelationType.PossibleDuplicate,
            Score = Math.Round(score, 3, MidpointRounding.ToEven),
            Signals = signalsJson,
            Explanation = Bound(explanation),
            CreatedAt = now,
        };
    }

    public static IncidentRelation LinkManually(Guid id, Guid a, Guid b, Guid userId, DateTimeOffset now)
    {
        var (source, target) = Order(a, b);
        return new IncidentRelation
        {
            Id = id,
            SourceIncidentId = source,
            TargetIncidentId = target,
            Type = RelationType.Related,
            Explanation = "Linked manually by a coordinator.",
            CreatedById = userId,
            CreatedAt = now,
            DecidedById = userId,
            DecidedAt = now,
        };
    }

    public Result MarkRelated(Guid userId, DateTimeOffset now) =>
        Decide(RelationType.Related, userId, now, RelationType.PossibleDuplicate, RelationType.Unrelated);

    public Result MarkUnrelated(Guid userId, DateTimeOffset now) =>
        Decide(RelationType.Unrelated, userId, now, RelationType.PossibleDuplicate, RelationType.Related);

    public Result ConfirmDuplicate(Guid primaryIncidentId, Guid userId, DateTimeOffset now)
    {
        if (!Involves(primaryIncidentId))
        {
            return Error.Validation("relation.primary_invalid", "The primary incident must be one of the pair.");
        }

        var result = Decide(RelationType.ConfirmedDuplicate, userId, now, RelationType.PossibleDuplicate, RelationType.Related, RelationType.Unrelated);
        if (result.IsSuccess)
        {
            PrimaryIncidentId = primaryIncidentId;
        }

        return result;
    }

    private Result Decide(RelationType to, Guid userId, DateTimeOffset now, params RelationType[] allowedFrom)
    {
        if (!allowedFrom.Contains(Type))
        {
            return Error.InvalidTransition("relation.invalid_decision", $"A {Type} relation cannot be changed to {to}.");
        }

        Type = to;
        DecidedById = userId;
        DecidedAt = now;
        return Result.Success();
    }

    private static string Bound(string value) => value.Length <= MaxExplanationLength ? value : value[..MaxExplanationLength];
}
