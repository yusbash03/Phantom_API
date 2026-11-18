using PHANTOM.Application.Abstractions;

namespace PHANTOM.Infrastructure.Common;

internal sealed class SystemClock(TimeProvider timeProvider) : IClock
{
    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
}
