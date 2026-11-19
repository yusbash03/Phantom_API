using PHANTOM.Domain.Incidents;

namespace PHANTOM.Application.Intelligence;

/// <summary>How strongly the report indicates something. Deliberately has no "confirmed" value: only people confirm.</summary>
public enum Indication
{
    Unknown,
    NotIndicated,
    Possible,
    Reported,
}

public enum EntityType
{
    Person,
    Vehicle,
    Organisation,
    Landmark,
    Hazard,
    Other,
}

public sealed record ExtractedEntity(EntityType Type, string Value);

/// <summary>
/// Schema <c>phantom.intel.v1</c> (Requirement 6.2). A recommendation for a coordinator, never a decision.
/// </summary>
public sealed record IntelligenceResultV1(
    IncidentCategory Category,
    string Summary,
    Severity Severity,
    string SeverityRationale,
    IReadOnlyList<string> NamedLocations,
    Indication PotentialCasualties,
    Indication PossibleTrappedPersons,
    IReadOnlyList<string> Hazards,
    IReadOnlyList<string> MissingInformation,
    IReadOnlyList<string> RecommendedNextActions,
    IReadOnlyList<ExtractedEntity> ExtractedEntities,
    string ConfidenceExplanation)
{
    public const int MaxSummaryLength = 1000;
    public const int MaxTextLength = 500;
    public const int MaxEntityValueLength = 200;
    public const int MaxItems = 20;
}
