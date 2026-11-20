using FluentValidation;
using PHANTOM.Application.Incidents;
using PHANTOM.Domain.Responders;

namespace PHANTOM.Application.Responders;

public sealed record ResponderListQuery(
    string? Team = null,
    string? Skill = null,
    ResponderAvailability? Availability = null,
    bool? IsActive = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 25);

public sealed record ResponderResponse(
    Guid Id,
    string CallSign,
    string FullName,
    string Team,
    string? Phone,
    string? Email,
    IReadOnlyList<string> Skills,
    ResponderAvailability Availability,
    bool IsActive,
    UserRef? LinkedUser,
    DateTimeOffset LastStatusAt,
    bool IsDemoData,
    DateTimeOffset CreatedAt,
    int Version);

/// <summary>Values for the team and skill filters.</summary>
public sealed record ResponderFacets(IReadOnlyList<string> Teams, IReadOnlyList<string> Skills);

public sealed record CreateResponderRequest(
    string CallSign, string FullName, string Team, string? Phone, string? Email, IReadOnlyList<string>? Skills, Guid? UserId);

public sealed record UpdateResponderRequest(
    string FullName, string Team, string? Phone, string? Email, IReadOnlyList<string>? Skills, Guid? UserId, int Version);

public sealed record ResponderVersionRequest(int Version);

/// <summary>Only Available or Unavailable; Assigned is derived from an active assignment.</summary>
public sealed record SetAvailabilityRequest(ResponderAvailability? Availability, int? Version);

internal sealed class CreateResponderRequestValidator : AbstractValidator<CreateResponderRequest>
{
    public CreateResponderRequestValidator()
    {
        RuleFor(r => r.CallSign).Must(c => Responder.NormaliseCallSign(c) is not null)
            .WithMessage("Call sign must be 2-20 letters, digits or hyphens (e.g. MED-07).");
        RuleFor(r => r.FullName).NotEmpty().WithMessage("Name is required.").MaximumLength(Responder.MaxNameLength);
        RuleFor(r => r.Team).NotEmpty().WithMessage("Team is required.").MaximumLength(Responder.MaxTeamLength);
        RuleFor(r => r.Phone).MaximumLength(Responder.MaxPhoneLength);
        RuleFor(r => r.Email).EmailAddress().WithMessage("Email address is not valid.").MaximumLength(Responder.MaxEmailLength)
            .When(r => !string.IsNullOrWhiteSpace(r.Email));
        RuleFor(r => r.Skills).Must(s => s is null || s.Count <= Responder.MaxSkills).WithMessage($"At most {Responder.MaxSkills} skills.");
        RuleForEach(r => r.Skills).MaximumLength(Responder.MaxSkillLength).WithMessage($"Each skill must be at most {Responder.MaxSkillLength} characters.");
    }
}

internal sealed class UpdateResponderRequestValidator : AbstractValidator<UpdateResponderRequest>
{
    public UpdateResponderRequestValidator()
    {
        RuleFor(r => r.FullName).NotEmpty().WithMessage("Name is required.").MaximumLength(Responder.MaxNameLength);
        RuleFor(r => r.Team).NotEmpty().WithMessage("Team is required.").MaximumLength(Responder.MaxTeamLength);
        RuleFor(r => r.Phone).MaximumLength(Responder.MaxPhoneLength);
        RuleFor(r => r.Email).EmailAddress().WithMessage("Email address is not valid.").MaximumLength(Responder.MaxEmailLength)
            .When(r => !string.IsNullOrWhiteSpace(r.Email));
        RuleFor(r => r.Skills).Must(s => s is null || s.Count <= Responder.MaxSkills).WithMessage($"At most {Responder.MaxSkills} skills.");
        RuleForEach(r => r.Skills).MaximumLength(Responder.MaxSkillLength).WithMessage($"Each skill must be at most {Responder.MaxSkillLength} characters.");
    }
}

internal sealed class SetAvailabilityRequestValidator : AbstractValidator<SetAvailabilityRequest>
{
    public SetAvailabilityRequestValidator() =>
        RuleFor(r => r.Availability).NotNull().WithMessage("Choose Available or Unavailable.")
            .Must(a => a is ResponderAvailability.Available or ResponderAvailability.Unavailable)
            .WithMessage("Choose Available or Unavailable; Assigned is set automatically.");
}
