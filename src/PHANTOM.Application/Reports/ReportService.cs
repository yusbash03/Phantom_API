using System.Globalization;
using System.Text;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using PHANTOM.Application.Abstractions;
using PHANTOM.Application.Common;
using PHANTOM.Domain.Common;
using PHANTOM.Domain.Incidents;
using Severity = PHANTOM.Domain.Incidents.Severity;

namespace PHANTOM.Application.Reports;

/// <summary>Incidents created in [From, To). Both are required so a report always states its period.</summary>
public sealed record ReportQuery(DateTimeOffset? From = null, DateTimeOffset? To = null);

public sealed record CountItem(string Key, int Count);

/// <summary>A median duration and how many incidents it was computed from (null when there were none).</summary>
public sealed record DurationStat(double? MedianSeconds, int SampleSize);

/// <summary>
/// How often the AI's recommended severity matched the coordinator's approved severity, over incidents that have
/// both. AiHigher/AiLower say which way it differed.
/// </summary>
public sealed record AiAgreement(int Compared, int Agreed, int AiHigher, int AiLower, double? Rate);

/// <summary>Requirement 16.1. Every value is computed from the database when requested (16.2).</summary>
public sealed record ReportSummary(
    DateTimeOffset From,
    DateTimeOffset To,
    int TotalIncidents,
    IReadOnlyList<CountItem> ByCategory,
    IReadOnlyList<CountItem> BySeverity,
    IReadOnlyList<CountItem> ByStatus,
    DurationStat ReportToVerification,
    DurationStat ReportToFirstAcknowledgement,
    DurationStat ReportToResolution,
    AiAgreement AiSeverityAgreement,
    DateTimeOffset GeneratedAt);

public interface IReportService
{
    Task<Result<ReportSummary>> SummaryAsync(ReportQuery query, CancellationToken cancellationToken);
}

internal sealed class ReportQueryValidator : AbstractValidator<ReportQuery>
{
    public const int MaxDays = 366;

    public ReportQueryValidator()
    {
        RuleFor(q => q.From).NotNull().WithMessage("Choose the start of the period.");
        RuleFor(q => q.To).NotNull().WithMessage("Choose the end of the period.");
        RuleFor(q => q.To).GreaterThan(q => q.From).When(q => q.From.HasValue && q.To.HasValue)
            .WithMessage("The end of the period must be after its start.");
        RuleFor(q => q).Must(q => (q.To!.Value - q.From!.Value).TotalDays <= MaxDays)
            .When(q => q.From.HasValue && q.To.HasValue && q.To > q.From)
            .WithName("To").WithMessage($"A report can cover at most {MaxDays} days.");
    }
}

internal sealed class ReportService(IPhantomDbContext db, IValidator<ReportQuery> validator, IClock clock) : IReportService
{
    /// <summary>Label for incidents a coordinator has not yet given a severity.</summary>
    public const string NotAssessed = "NotAssessed";

    public async Task<Result<ReportSummary>> SummaryAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var validation = await validator.ValidateAsync(query, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationFailedError.From(validation);
        }

        var from = query.From!.Value.ToUniversalTime();
        var to = query.To!.Value.ToUniversalTime();
        var incidents = await db.Incidents.AsNoTracking()
            .Where(i => i.CreatedAt >= from && i.CreatedAt < to)
            .Select(i => new { i.Id, i.Category, i.Status, i.ApprovedSeverity, i.AiRecommendedSeverity, i.CreatedAt, i.VerifiedAt, i.ResolvedAt })
            .ToListAsync(cancellationToken);

        var ids = incidents.Select(i => i.Id).ToList();
        var firstAcknowledged = await db.ResponderAssignments.AsNoTracking()
            .Where(a => ids.Contains(a.IncidentId) && a.AcknowledgedAt != null)
            .GroupBy(a => a.IncidentId)
            .Select(g => new { IncidentId = g.Key, At = g.Min(a => a.AcknowledgedAt) })
            .ToDictionaryAsync(x => x.IncidentId, x => x.At!.Value, cancellationToken);

        static IReadOnlyList<CountItem> Count<T>(IEnumerable<T> values, IEnumerable<T> allKeys, Func<T, string> name) where T : notnull
        {
            var counts = values.GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count());
            return allKeys.Select(k => new CountItem(name(k), counts.GetValueOrDefault(k))).ToList();
        }

        var severityKeys = Enum.GetValues<Severity>().Select(s => s.ToString()).Append(NotAssessed);
        var compared = incidents.Where(i => i.ApprovedSeverity is not null && i.AiRecommendedSeverity is not null).ToList();
        var agreed = compared.Count(i => i.ApprovedSeverity == i.AiRecommendedSeverity);

        return new ReportSummary(
            from,
            to,
            incidents.Count,
            Count(incidents.Select(i => i.Category), Enum.GetValues<IncidentCategory>(), c => c.ToString()),
            Count(incidents.Select(i => i.ApprovedSeverity?.ToString() ?? NotAssessed), severityKeys, s => s),
            Count(incidents.Select(i => i.Status), Enum.GetValues<IncidentStatus>(), s => s.ToString()),
            Median(incidents.Where(i => i.VerifiedAt is not null).Select(i => i.VerifiedAt!.Value - i.CreatedAt)),
            Median(incidents.Where(i => firstAcknowledged.ContainsKey(i.Id)).Select(i => firstAcknowledged[i.Id] - i.CreatedAt)),
            Median(incidents.Where(i => i.ResolvedAt is not null).Select(i => i.ResolvedAt!.Value - i.CreatedAt)),
            new AiAgreement(
                compared.Count,
                agreed,
                compared.Count(i => i.AiRecommendedSeverity > i.ApprovedSeverity),
                compared.Count(i => i.AiRecommendedSeverity < i.ApprovedSeverity),
                compared.Count == 0 ? null : Math.Round((double)agreed / compared.Count, 4)),
            clock.UtcNow);
    }

    internal static DurationStat Median(IEnumerable<TimeSpan> durations)
    {
        var seconds = durations.Select(d => Math.Max(0, d.TotalSeconds)).Order().ToList();
        if (seconds.Count == 0)
        {
            return new DurationStat(null, 0);
        }

        var middle = seconds.Count / 2;
        var median = seconds.Count % 2 == 1 ? seconds[middle] : (seconds[middle - 1] + seconds[middle]) / 2;
        return new DurationStat(Math.Round(median, 1), seconds.Count);
    }
}

/// <summary>
/// CSV for spreadsheets (Requirement 16.2): one row per figure, RFC 4180 quoting, and any cell that a spreadsheet
/// would read as a formula (=, +, -, @, tab, CR) prefixed with an apostrophe.
/// </summary>
public static class ReportCsv
{
    public static string Write(ReportSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var csv = new StringBuilder();
        void Row(params string?[] cells) => csv.Append(string.Join(',', cells.Select(Cell))).Append("\r\n");
        static string Number(double? value) => value?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty;

        Row("section", "item", "value", "sample_size");
        Row("period", "from_utc", summary.From.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), null);
        Row("period", "to_utc", summary.To.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), null);
        Row("period", "generated_at_utc", summary.GeneratedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), null);
        Row("incidents", "total", summary.TotalIncidents.ToString(CultureInfo.InvariantCulture), null);
        foreach (var (section, items) in new[] { ("category", summary.ByCategory), ("approved_severity", summary.BySeverity), ("status", summary.ByStatus) })
        {
            foreach (var item in items)
            {
                Row(section, item.Key, item.Count.ToString(CultureInfo.InvariantCulture), null);
            }
        }

        foreach (var (name, stat) in new[]
        {
            ("report_to_verification", summary.ReportToVerification),
            ("report_to_first_acknowledgement", summary.ReportToFirstAcknowledgement),
            ("report_to_resolution", summary.ReportToResolution),
        })
        {
            Row("median_seconds", name, Number(stat.MedianSeconds), stat.SampleSize.ToString(CultureInfo.InvariantCulture));
        }

        var ai = summary.AiSeverityAgreement;
        Row("ai_severity_agreement", "rate", Number(ai.Rate), ai.Compared.ToString(CultureInfo.InvariantCulture));
        Row("ai_severity_agreement", "agreed", ai.Agreed.ToString(CultureInfo.InvariantCulture), null);
        Row("ai_severity_agreement", "ai_higher", ai.AiHigher.ToString(CultureInfo.InvariantCulture), null);
        Row("ai_severity_agreement", "ai_lower", ai.AiLower.ToString(CultureInfo.InvariantCulture), null);
        return csv.ToString();
    }

    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var safe = value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
        return safe.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{safe.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : safe;
    }
}
