namespace PHANTOM.Application.Audit;

/// <summary>
/// Adds an audit record to the current unit of work. It is persisted by the same
/// <c>SaveChangesAsync</c> call as the change it describes, so both commit or neither does.
/// </summary>
public interface IAuditWriter
{
    void Record(string action, string entityType, string? entityId, object? previousValue = null, object? newValue = null, object? metadata = null);

    /// <summary>For background work with no human caller.</summary>
    void RecordSystem(string action, string entityType, string? entityId, object? previousValue = null, object? newValue = null, object? metadata = null);
}
