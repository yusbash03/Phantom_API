using FluentValidation;
using PHANTOM.Domain.Incidents;

namespace PHANTOM.Application.Reporting.Intake;

/// <summary>
/// Field validation for public submissions. File type and per-kind counts depend on content sniffing,
/// so <see cref="ReportIntakeService"/> enforces them; only the overall count is checked here.
/// </summary>
internal sealed class SubmitReportValidator : AbstractValidator<SubmitReportRequest>
{
    public SubmitReportValidator()
    {
        RuleFor(r => r.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MaximumLength(IncidentReport.MaxTitleLength).WithMessage($"Title must be at most {IncidentReport.MaxTitleLength} characters.");

        RuleFor(r => r.Description)
            .NotEmpty().WithMessage("Description is required.")
            .MaximumLength(IncidentReport.MaxDescriptionLength).WithMessage($"Description must be at most {IncidentReport.MaxDescriptionLength} characters.");

        RuleFor(r => r.Category)
            .IsInEnum().WithMessage("Category is not valid.");

        RuleFor(r => r.LocationText)
            .NotEmpty().WithMessage("Location description is required.")
            .MaximumLength(IncidentReport.MaxLocationTextLength).WithMessage($"Location description must be at most {IncidentReport.MaxLocationTextLength} characters.");

        RuleFor(r => r.Latitude)
            .NotNull().When(r => r.Longitude.HasValue).WithMessage("Latitude and longitude must be provided together.")
            .InclusiveBetween(-90m, 90m).WithMessage("Latitude must be between -90 and 90.");

        RuleFor(r => r.Longitude)
            .NotNull().When(r => r.Latitude.HasValue).WithMessage("Latitude and longitude must be provided together.")
            .InclusiveBetween(-180m, 180m).WithMessage("Longitude must be between -180 and 180.");

        RuleFor(r => r.ReporterName)
            .MaximumLength(IncidentReport.MaxNameLength).WithMessage($"Name must be at most {IncidentReport.MaxNameLength} characters.");

        RuleFor(r => r.ReporterPhone)
            .MaximumLength(IncidentReport.MaxPhoneLength).WithMessage($"Phone must be at most {IncidentReport.MaxPhoneLength} characters.");

        RuleFor(r => r.ReporterEmail)
            .MaximumLength(IncidentReport.MaxEmailLength).WithMessage($"Email must be at most {IncidentReport.MaxEmailLength} characters.")
            .EmailAddress().WithMessage("Email address is not valid.")
            .When(r => !string.IsNullOrWhiteSpace(r.ReporterEmail));

        RuleFor(r => r.IdempotencyKey)
            .Must(k => k is null || Guid.TryParse(k, out _)).WithMessage("Idempotency-Key must be a UUID.");

        RuleFor(r => r.Files)
            .Must(files => files.Count <= ReportIntakeService.MaxFiles)
            .WithMessage($"At most {ReportIntakeService.MaxImages} images and {ReportIntakeService.MaxAudioFiles} audio recording can be attached.");
    }
}
