using FluentValidation.Results;
using PHANTOM.Domain.Common;

namespace PHANTOM.Application.Common;

/// <summary>Carries field-level validation failures through the Result pipeline.</summary>
public sealed class ValidationFailedError
{
    private ValidationFailedError()
    {
    }

    public const string Code = "validation.failed";

    public static Error From(ValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var fields = result.Errors
            .GroupBy(e => ToCamelCase(e.PropertyName))
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
        return Error.Validation(Code, "One or more fields are invalid.") with { Fields = fields };
    }

    private static string ToCamelCase(string name) =>
        string.IsNullOrEmpty(name) || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
