using FsCheck.Xunit;
using PHANTOM.Domain.Incidents;

namespace PHANTOM.UnitTests.Domain;

/// <summary>Critical property 2: an AI recommendation cannot alter approved severity or status.</summary>
public sealed class AiRecommendationIsolationTests
{
    [Property(MaxTest = 300)]
    public bool Ai_recommendation_never_changes_approved_values(IncidentStatus status, Severity aiSeverity, IncidentCategory aiCategory)
    {
        var incident = IncidentTestBuilder.At(status);
        var approvedBefore = incident.ApprovedSeverity;
        var categoryBefore = incident.Category;
        var resolutionBefore = incident.ResolutionSummary;

        incident.RecordAiRecommendation(aiSeverity, aiCategory, IncidentTestBuilder.Now);

        return incident.ApprovedSeverity == approvedBefore
            && incident.Status == status
            && incident.Category == categoryBefore
            && incident.ResolutionSummary == resolutionBefore
            && incident.AiRecommendedSeverity == aiSeverity
            && incident.AiRecommendedCategory == aiCategory;
    }
}
