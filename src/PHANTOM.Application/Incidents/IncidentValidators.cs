using FluentValidation;
using PHANTOM.Domain.Incidents;

namespace PHANTOM.Application.Incidents;

internal sealed class EditIncidentRequestValidator : AbstractValidator<EditIncidentRequest>
{
    public EditIncidentRequestValidator()
    {
        RuleFor(r => r.Title).NotEmpty().WithMessage("Title is required.")
            .MaximumLength(Incident.MaxTitleLength).WithMessage($"Title must be at most {Incident.MaxTitleLength} characters.");
        RuleFor(r => r.Description).NotEmpty().WithMessage("Description is required.")
            .MaximumLength(Incident.MaxDescriptionLength).WithMessage($"Description must be at most {Incident.MaxDescriptionLength} characters.");
        RuleFor(r => r.Category).NotNull().WithMessage("Category is required.").IsInEnum().WithMessage("Category is not valid.");
    }
}

internal sealed class VerifyIncidentRequestValidator : AbstractValidator<VerifyIncidentRequest>
{
    public VerifyIncidentRequestValidator() =>
        RuleFor(r => r.ApprovedSeverity).NotNull().WithMessage("An approved severity is required.").IsInEnum().WithMessage("An approved severity is required.");
}

internal sealed class ReasonRequestValidator : AbstractValidator<ReasonRequest>
{
    public ReasonRequestValidator() =>
        RuleFor(r => r.Reason).NotEmpty().WithMessage("A reason is required.")
            .MaximumLength(Incident.MaxReasonLength).WithMessage($"Reason must be at most {Incident.MaxReasonLength} characters.");
}

internal sealed class ResolveIncidentRequestValidator : AbstractValidator<ResolveIncidentRequest>
{
    public ResolveIncidentRequestValidator() =>
        RuleFor(r => r.ResolutionSummary).NotEmpty().WithMessage("Resolution details are required.")
            .MaximumLength(Incident.MaxDescriptionLength).WithMessage($"Resolution details must be at most {Incident.MaxDescriptionLength} characters.");
}

internal sealed class ChangeSeverityRequestValidator : AbstractValidator<ChangeSeverityRequest>
{
    public ChangeSeverityRequestValidator() =>
        RuleFor(r => r.Severity).NotNull().WithMessage("Severity is required.").IsInEnum().WithMessage("Severity is not valid.");
}

internal sealed class IncidentListQueryValidator : AbstractValidator<IncidentListQuery>
{
    public IncidentListQueryValidator()
    {
        RuleFor(q => q.To).GreaterThanOrEqualTo(q => q.From).When(q => q.From.HasValue && q.To.HasValue)
            .WithMessage("The end of the date range must be after its start.");
        RuleFor(q => q.Search).MaximumLength(200).WithMessage("Search must be at most 200 characters.");
        RuleForEach(q => q.Status).IsInEnum();
        RuleFor(q => q.Category).IsInEnum();
        RuleFor(q => q.Severity).IsInEnum();
    }
}
