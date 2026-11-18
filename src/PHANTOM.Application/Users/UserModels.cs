namespace PHANTOM.Application.Users;

public sealed record CreateUserRequest(string Email, string DisplayName, string Password, IReadOnlyList<string> Roles);

public sealed record UpdateUserRolesRequest(IReadOnlyList<string> Roles);

public sealed record UserListQuery(string? Role = null, bool? IsActive = null, string? Search = null, int Page = 1, int PageSize = 25);

public sealed record UserResponse(
    Guid Id,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Roles,
    bool IsActive,
    bool IsDemoData,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);
