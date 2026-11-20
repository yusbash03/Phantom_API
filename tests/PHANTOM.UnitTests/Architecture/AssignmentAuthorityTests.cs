using System.Reflection;
using System.Reflection.Emit;
using PHANTOM.Domain.Assignments;

namespace PHANTOM.UnitTests.Architecture;

/// <summary>
/// Requirement 11.7 / product principle 1: AI (or any other automation) never creates an assignment.
/// The only code that may call <see cref="ResponderAssignment.Create"/> is the coordinator-facing
/// AssignmentService, which requires an authenticated coordinator.
/// </summary>
public sealed class AssignmentAuthorityTests
{
    private static readonly Assembly[] Scanned =
    [
        typeof(PHANTOM.Application.DependencyInjection).Assembly,
        typeof(PHANTOM.Infrastructure.DependencyInjection).Assembly,
    ];

    private static readonly OpCode[] CallOpCodes = [OpCodes.Call, OpCodes.Callvirt, OpCodes.Newobj, OpCodes.Ldftn];

    [Fact]
    public void Only_the_assignment_service_creates_assignments()
    {
        var create = typeof(ResponderAssignment).GetMethod(nameof(ResponderAssignment.Create))!;
        var callers = Scanned
            .SelectMany(a => a.GetTypes())
            .Where(t => Calls(t, create))
            .Select(OuterMost)
            .Select(t => t.FullName)
            .Distinct()
            .ToList();

        Assert.Equal(["PHANTOM.Application.Assignments.AssignmentService"], callers);
    }

    [Fact]
    public void Ai_and_correlation_services_have_no_access_to_assignments()
    {
        var application = typeof(PHANTOM.Application.DependencyInjection).Assembly;
        var assignmentService = application.GetType("PHANTOM.Application.Assignments.IAssignmentService")!;
        var automated = application.GetTypes().Where(t =>
            t.Namespace is "PHANTOM.Application.Intelligence" or "PHANTOM.Application.Correlation" or "PHANTOM.Application.Geocoding");

        foreach (var type in automated)
        {
            var dependencies = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters()).Select(p => p.ParameterType);
            Assert.DoesNotContain(assignmentService, dependencies);
        }
    }

    [Fact]
    public void An_assignment_requires_an_identified_coordinator()
    {
        Assert.Throws<ArgumentException>(() => ResponderAssignment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, DateTimeOffset.UtcNow));
    }

    private static Type OuterMost(Type type)
    {
        while (type.DeclaringType is not null)
        {
            type = type.DeclaringType;
        }

        return type;
    }

    /// <summary>Scans method bodies for call/newobj instructions whose operand resolves to <paramref name="target"/>.</summary>
    private static bool Calls(Type type, MethodBase target)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        IEnumerable<MethodBase> methods = type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all));
        foreach (var method in methods)
        {
            byte[]? il;
            try
            {
                il = method.GetMethodBody()?.GetILAsByteArray();
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (il is null)
            {
                continue;
            }

            for (var i = 0; i + 4 < il.Length; i++)
            {
                if (!CallOpCodes.Any(op => op.Size == 1 && il[i] == (byte)op.Value) && !(il[i] == 0xFE && il[i + 1] == 0x06))
                {
                    continue;
                }

                var offset = il[i] == 0xFE ? i + 2 : i + 1;
                if (offset + 4 > il.Length)
                {
                    continue;
                }

                var token = BitConverter.ToInt32(il, offset);
                try
                {
                    var resolved = type.Module.ResolveMethod(token, type.IsGenericType ? type.GetGenericArguments() : null, method.IsGenericMethod ? method.GetGenericArguments() : null);
                    if (resolved == target)
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException or BadImageFormatException or MissingMethodException or TypeLoadException)
                {
                    // Not a method token at this position; keep scanning.
                }
            }
        }

        return false;
    }
}
