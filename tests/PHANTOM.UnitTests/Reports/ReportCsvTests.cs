using PHANTOM.Application.Reports;

namespace PHANTOM.UnitTests.Reports;

/// <summary>CSV export safety and the median used in reports (Requirement 16).</summary>
public sealed class ReportCsvTests
{
    [Theory]
    [InlineData("Fire", "Fire")]
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("+1", "'+1")]
    [InlineData("-5", "'-5")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    [InlineData("", "")]
    public void Cells_are_quoted_and_never_run_as_formulas(string value, string expected) => Assert.Equal(expected, ReportCsv.Cell(value));

    [Fact]
    public void Writes_a_header_and_one_row_per_figure()
    {
        var summary = new ReportSummary(
            new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero), 4,
            [new CountItem("Fire", 3), new CountItem("Flood", 1)], [new CountItem("High", 2), new CountItem("NotAssessed", 2)], [new CountItem("Reported", 4)],
            new DurationStat(90.5, 2), new DurationStat(null, 0), new DurationStat(3600, 1), new AiAgreement(2, 1, 1, 0, 0.5),
            new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.Zero));

        var lines = ReportCsv.Write(summary).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("section,item,value,sample_size", lines[0]);
        Assert.Contains("incidents,total,4,", lines);
        Assert.Contains("category,Flood,1,", lines);
        Assert.Contains("approved_severity,NotAssessed,2,", lines);
        Assert.Contains("median_seconds,report_to_verification,90.5,2", lines);
        Assert.Contains("median_seconds,report_to_first_acknowledgement,,0", lines);
        Assert.Contains("ai_severity_agreement,rate,0.5,2", lines);
        Assert.Contains("ai_severity_agreement,ai_higher,1,", lines);
    }

    [Fact]
    public void The_median_handles_odd_even_and_empty_samples()
    {
        Assert.Equal(new DurationStat(60, 3), ReportService.Median([TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1)]));
        Assert.Equal(new DurationStat(45, 2), ReportService.Median([TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1)]));
        Assert.Equal(new DurationStat(null, 0), ReportService.Median([]));
        // Clock skew never yields a negative duration.
        Assert.Equal(new DurationStat(0, 1), ReportService.Median([TimeSpan.FromSeconds(-2)]));
    }
}
