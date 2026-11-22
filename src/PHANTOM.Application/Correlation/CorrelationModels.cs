using FluentValidation;
using PHANTOM.Application.Incidents;
using PHANTOM.Domain.Correlation;
using PHANTOM.Domain.Incidents;
using Severity = PHANTOM.Domain.Incidents.Severity;

namespace PHANTOM.Application.Correlation;

/// <summary>Configuration section <c>Correlation</c>: the defaults (design §6). Administrators override them in Settings (task 34).</summary>
public sealed class CorrelationOptions
{
    public const string SectionName = "Correlation";

    public double GeoWeight { get; set; } = 0.35;
    public double TimeWeight { get; set; } = 0.15;
    public double CategoryWeight { get; set; } = 0.15;
    public double TextWeight { get; set; } = 0.20;
    public double LocationWeight { get; set; } = 0.15;
    public double GeoRadiusMeters { get; set; } = 1500;
    public double WindowHours { get; set; } = 6;
    public double SuggestThreshold { get; set; } = 0.55;
    public int MaxCandidates { get; set; } = 100;

    public CorrelationSettings ToSettings() => new()
    {
        GeoWeight = GeoWeight,
        TimeWeight = TimeWeight,
        CategoryWeight = CategoryWeight,
        TextWeight = TextWeight,
        LocationWeight = LocationWeight,
        GeoRadiusMeters = GeoRadiusMeters,
        Window = TimeSpan.FromHours(WindowHours),
        SuggestThreshold = SuggestThreshold,
        MaxCandidates = MaxCandidates,
    };

    public bool IsValid() =>
        new[] { GeoWeight, TimeWeight, CategoryWeight, TextWeight, LocationWeight }.All(w => w is >= 0 and <= 1)
        && GeoWeight + TimeWeight + CategoryWeight + TextWeight + LocationWeight > 0
        && GeoRadiusMeters is > 0 and <= 100_000
        && WindowHours is > 0 and <= 168
        && SuggestThreshold is > 0 and <= 1
        && MaxCandidates is >= 1 and <= 1000;
}

public enum RelationDecision
{
    ConfirmDuplicate,
    Related,
    Unrelated,
}

/// <summary>Signal breakdown stored with a suggestion and shown to coordinators.</summary>
public sealed record CorrelationSignals(
    SignalScore Geo,
    SignalScore Time,
    SignalScore Category,
    SignalScore Text,
    SignalScore Locations,
    double? DistanceMeters,
    double MinutesApart,
    IReadOnlyList<string> SharedPlaceTokens)
{
    public static CorrelationSignals From(CorrelationResult r) =>
        new(r.Geo, r.Time, r.Category, r.Text, r.Locations, r.DistanceMeters, r.MinutesApart, r.SharedPlaceTokens);
}

public sealed record RelatedIncidentSummary(
    Guid Id,
    string Number,
    string Title,
    IncidentStatus Status,
    IncidentCategory Category,
    Severity? ApprovedSeverity,
    string LocationText,
    DateTimeOffset CreatedAt);

/// <summary>A relation seen from one incident. <c>Other</c> is the incident on the far side of the pair.</summary>
public sealed record RelationResponse(
    Guid Id,
    RelationType Type,
    decimal? Score,
    string? Explanation,
    CorrelationSignals? Signals,
    RelatedIncidentSummary Other,
    Guid? PrimaryIncidentId,
    UserRef? CreatedBy,
    DateTimeOffset CreatedAt,
    UserRef? DecidedBy,
    DateTimeOffset? DecidedAt,
    IReadOnlyList<RelationDecision> AvailableDecisions,
    IReadOnlyList<Guid> ConfirmablePrimaryIds);

public sealed record LinkIncidentRequest(string OtherIncidentNumber);

/// <summary><c>PrimaryIncidentId</c> is required to confirm a duplicate: it stays open and receives the reports.</summary>
public sealed record RelationDecisionRequest(RelationDecision? Decision, Guid? PrimaryIncidentId);

public sealed record RelationDecisionResponse(Guid Id, RelationType Type, Guid? PrimaryIncidentId, DateTimeOffset? DecidedAt);

internal sealed class LinkIncidentRequestValidator : AbstractValidator<LinkIncidentRequest>
{
    public LinkIncidentRequestValidator() =>
        RuleFor(r => r.OtherIncidentNumber).NotEmpty().WithMessage("Enter the incident number to link.")
            .MaximumLength(IncidentReport.MaxReferenceLength).WithMessage("That is not a valid incident number.");
}

internal sealed class RelationDecisionRequestValidator : AbstractValidator<RelationDecisionRequest>
{
    public RelationDecisionRequestValidator()
    {
        RuleFor(r => r.Decision).NotNull().WithMessage("A decision is required.").IsInEnum();
        RuleFor(r => r.PrimaryIncidentId).NotNull()
            .When(r => r.Decision == RelationDecision.ConfirmDuplicate)
            .WithMessage("Choose which incident stays open (the primary).");
    }
}
