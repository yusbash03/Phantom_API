using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PHANTOM.API.Infrastructure;
using PHANTOM.Application.Reports;

namespace PHANTOM.API.Controllers;

/// <summary>Summary reports (Requirement 16), computed from the database on request. Read-only.</summary>
[ApiController]
[Route("api/v1/reports")]
[Authorize(Policy = Policies.Staff)]
public sealed class ReportsController(IReportService reports) : ControllerBase
{
    [HttpGet("summary")]
    [Produces("application/json")]
    [ProducesResponseType<ReportSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ReportSummary>> Summary([FromQuery] ReportQuery query, CancellationToken cancellationToken) =>
        this.ToActionResult(await reports.SummaryAsync(query, cancellationToken));

    /// <summary>The same summary as CSV: one row per figure (section, item, value, sample_size).</summary>
    [HttpGet("summary.csv")]
    [Produces("text/csv", "application/problem+json")]
    [ProducesResponseType<FileContentResult>(StatusCodes.Status200OK, "text/csv")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SummaryCsv([FromQuery] ReportQuery query, CancellationToken cancellationToken)
    {
        var result = await reports.SummaryAsync(query, cancellationToken);
        if (!result.IsSuccess)
        {
            return this.Problem(result.Error!);
        }

        var name = string.Create(CultureInfo.InvariantCulture, $"phantom-summary-{result.Value.From:yyyy-MM-dd}-to-{result.Value.To:yyyy-MM-dd}.csv");
        // A BOM lets spreadsheet apps detect UTF-8.
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ReportCsv.Write(result.Value))).ToArray(), "text/csv; charset=utf-8", name);
    }
}
