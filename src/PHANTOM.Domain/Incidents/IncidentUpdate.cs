namespace PHANTOM.Domain.Incidents;

public enum IncidentUpdateKind
{
    Note,
    ProgressUpdate,
    ResolutionNote,
}

/// <summary>An append-only note on an incident, e.g. a responder's progress update (Requirement 11.6).</summary>
public sealed class IncidentUpdate
{
    public const int MaxBodyLength = 2000;

    private IncidentUpdate()
    {
        // EF Core
        Body = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid IncidentId { get; private set; }
    public Guid? AssignmentId { get; private set; }
    public Guid AuthorId { get; private set; }
    public IncidentUpdateKind Kind { get; private set; }
    public string Body { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static IncidentUpdate Create(Guid id, Guid incidentId, Guid? assignmentId, Guid authorId, IncidentUpdateKind kind, string body, DateTimeOffset now)
    {
        if (authorId == Guid.Empty)
        {
            throw new ArgumentException("An update needs an author.", nameof(authorId));
        }

        if (string.IsNullOrWhiteSpace(body) || body.Trim().Length > MaxBodyLength)
        {
            throw new ArgumentException($"An update needs text of at most {MaxBodyLength} characters.", nameof(body));
        }

        return new IncidentUpdate
        {
            Id = id,
            IncidentId = incidentId,
            AssignmentId = assignmentId,
            AuthorId = authorId,
            Kind = kind,
            Body = body.Trim(),
            CreatedAt = now,
        };
    }
}
