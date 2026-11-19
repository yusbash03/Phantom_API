namespace PHANTOM.Application.Abstractions;

/// <summary>Generates unique, human-readable incident numbers (e.g. PH-2026-000042) from a database sequence.</summary>
public interface IIncidentNumberGenerator
{
    Task<string> NextAsync(CancellationToken cancellationToken = default);
}
