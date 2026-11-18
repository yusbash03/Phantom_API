namespace PHANTOM.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
