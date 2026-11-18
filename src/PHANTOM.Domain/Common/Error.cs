namespace PHANTOM.Domain.Common;

public enum ErrorKind
{
    Validation,
    NotFound,
    Forbidden,
    Conflict,
    InvalidTransition,
    Unauthorized,
}

/// <summary>An expected, typed failure. Exceptions are reserved for unexpected failures.</summary>
public sealed record Error(ErrorKind Kind, string Code, string Message)
{
    /// <summary>Optional field-level messages for validation failures (field name → messages).</summary>
    public IReadOnlyDictionary<string, string[]>? Fields { get; init; }

    public static Error Validation(string code, string message) => new(ErrorKind.Validation, code, message);
    public static Error NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);
    public static Error Forbidden(string code, string message) => new(ErrorKind.Forbidden, code, message);
    public static Error Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);
    public static Error InvalidTransition(string code, string message) => new(ErrorKind.InvalidTransition, code, message);
    public static Error Unauthorized(string code, string message) => new(ErrorKind.Unauthorized, code, message);
}
