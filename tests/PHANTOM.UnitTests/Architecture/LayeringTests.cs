using System.Reflection;

namespace PHANTOM.UnitTests.Architecture;

/// <summary>Enforces Clean Architecture dependency direction (architecture steering).</summary>
public sealed class LayeringTests
{
    private static readonly Assembly Domain = typeof(PHANTOM.Domain.Incidents.Incident).Assembly;
    private static readonly Assembly Application = typeof(PHANTOM.Application.DependencyInjection).Assembly;
    private static readonly Assembly Infrastructure = typeof(PHANTOM.Infrastructure.DependencyInjection).Assembly;

    private static string[] References(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    [Fact]
    public void Domain_depends_only_on_the_base_class_library()
    {
        var offenders = References(Domain).Where(r => !(r.StartsWith("System", StringComparison.Ordinal) || r == "netstandard" || r == "mscorlib"));
        Assert.Empty(offenders);
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure_api_or_providers()
    {
        var refs = References(Application);
        Assert.DoesNotContain("PHANTOM.Infrastructure", refs);
        Assert.DoesNotContain("PHANTOM.API", refs);
        Assert.DoesNotContain(refs, r => r.StartsWith("Npgsql", StringComparison.Ordinal));
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_api()
    {
        Assert.DoesNotContain("PHANTOM.API", References(Infrastructure));
    }
}
