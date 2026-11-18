namespace PHANTOM.Domain.Incidents;

public enum IncidentCategory
{
    Fire,
    MedicalEmergency,
    RoadAccident,
    Security,
    Flood,
    Infrastructure,
    Other,
}

public enum IncidentStatus
{
    Reported,
    UnderReview,
    Verified,
    Assigned,
    InProgress,
    Resolved,
    Closed,
    Rejected,
}

public enum Severity
{
    Low,
    Medium,
    High,
    Critical,
}

public enum LocationProvenance
{
    Reported,
    AiInferred,
    Geocoded,
    ManuallyVerified,
}

public enum LocationPrecision
{
    Unknown,
    Region,
    Locality,
    Street,
    Exact,
}
