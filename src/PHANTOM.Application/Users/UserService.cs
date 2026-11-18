using FluentValidation;
using Microsoft.EntityFrameworkCore;
using PHANTOM.Application.Abstractions;
using PHANTOM.Application.Audit;
using PHANTOM.Application.Common;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Common;
using PHANTOM.Domain.Identity;

namespace PHANTOM.Application.Users;

public interface IUserService
{
    Task<PagedResult<UserResponse>> ListAsync(UserListQuery query, CancellationToken cancellationToken);
    Task<Result<UserResponse>> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<Result<UserResponse>> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken);
    Task<Result<UserResponse>> SetRolesAsync(Guid id, UpdateUserRolesRequest request, CancellationToken cancellationToken);
    Task<Result<UserResponse>> DeactivateAsync(Guid id, CancellationToken cancellationToken);
    Task<Result<UserResponse>> ActivateAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class UserService(
    IPhantomDbContext db,
    IPasswordHasher passwordHasher,
    IAuditWriter audit,
    IClock clock,
    IValidator<CreateUserRequest> createValidator,
    IValidator<UpdateUserRolesRequest> rolesValidator) : IUserService
{
    private static readonly Error NotFound = Error.NotFound("user.not_found", "User not found.");
    private static readonly Error LastAdministrator =
        Error.Conflict("user.last_administrator", "At least one active Administrator must remain.");

    public async Task<PagedResult<UserResponse>> ListAsync(UserListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var users = db.Users.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Role))
        {
            users = users.Where(u => u.Roles.Any(r => r.Role!.Name == query.Role));
        }

        if (query.IsActive.HasValue)
        {
            users = users.Where(u => u.IsActive == query.IsActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
#pragma warning disable CA1304, CA1311, CA1862 // Expression tree translated to SQL lower()/LIKE, not executed in .NET.
            users = users.Where(u => u.Email.Contains(term) || u.DisplayName.ToLower().Contains(term));
#pragma warning restore CA1304, CA1311, CA1862
        }

        var total = await users.CountAsync(cancellationToken);
        var items = await users
            .OrderBy(u => u.DisplayName).ThenBy(u => u.Id)
            .Skip(page.Skip).Take(page.SafePageSize)
            .Select(u => new UserResponse(
                u.Id, u.Email, u.DisplayName,
                u.Roles.Select(r => r.Role!.Name).OrderBy(n => n).ToList(),
                u.IsActive, u.IsDemoData, u.CreatedAt, u.LastLoginAt))
            .ToListAsync(cancellationToken);

        return new PagedResult<UserResponse>(items, page.SafePage, page.SafePageSize, total);
    }

    public async Task<Result<UserResponse>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = await LoadAsync(id, tracking: false, cancellationToken);
        return user is null ? NotFound : ToResponse(user);
    }

    public async Task<Result<UserResponse>> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        var validation = await createValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationFailedError.From(validation);
        }

        var email = User.NormalizeEmail(request.Email);
        if (await db.Users.AnyAsync(u => u.Email == email, cancellationToken))
        {
            return Error.Conflict("user.email_taken", "A user with this email already exists.");
        }

        var roleIds = await ResolveRoleIdsAsync(request.Roles, cancellationToken);
        var user = User.Create(Guid.CreateVersion7(), email, request.DisplayName, clock.UtcNow);
        user.SetPasswordHash(passwordHasher.Hash(user, request.Password));
        user.SetRoles(roleIds);
        db.Users.Add(user);

        audit.Record(AuditActions.UserCreated, nameof(User), user.Id.ToString(),
            newValue: new { user.Email, user.DisplayName, Roles = request.Roles.Order(StringComparer.Ordinal).ToList() });
        await db.SaveChangesAsync(cancellationToken);

        return await GetAsync(user.Id, cancellationToken);
    }

    public async Task<Result<UserResponse>> SetRolesAsync(Guid id, UpdateUserRolesRequest request, CancellationToken cancellationToken)
    {
        var validation = await rolesValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationFailedError.From(validation);
        }

        var user = await LoadAsync(id, tracking: true, cancellationToken);
        if (user is null)
        {
            return NotFound;
        }

        var previous = RoleNamesOf(user);
        var removesAdmin = user.HasRole(Role.AdministratorId) && !request.Roles.Contains(RoleNames.Administrator);
        if (removesAdmin && user.IsActive && await IsLastActiveAdministratorAsync(user.Id, cancellationToken))
        {
            return LastAdministrator;
        }

        user.SetRoles(await ResolveRoleIdsAsync(request.Roles, cancellationToken));
        var next = request.Roles.Distinct().Order(StringComparer.Ordinal).ToList();
        audit.Record(AuditActions.UserRolesChanged, nameof(User), user.Id.ToString(), new { Roles = previous }, new { Roles = next });
        await RevokeSessionsAsync(user.Id, "roles_changed", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return await GetAsync(user.Id, cancellationToken);
    }

    public async Task<Result<UserResponse>> DeactivateAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = await LoadAsync(id, tracking: true, cancellationToken);
        if (user is null)
        {
            return NotFound;
        }

        if (!user.IsActive)
        {
            return ToResponse(user);
        }

        if (user.HasRole(Role.AdministratorId) && await IsLastActiveAdministratorAsync(user.Id, cancellationToken))
        {
            return LastAdministrator;
        }

        user.Deactivate();
        await RevokeSessionsAsync(user.Id, "user_deactivated", cancellationToken);
        audit.Record(AuditActions.UserDeactivated, nameof(User), user.Id.ToString(), new { IsActive = true }, new { IsActive = false });
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(user);
    }

    public async Task<Result<UserResponse>> ActivateAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = await LoadAsync(id, tracking: true, cancellationToken);
        if (user is null)
        {
            return NotFound;
        }

        if (!user.IsActive)
        {
            user.Activate();
            audit.Record(AuditActions.UserActivated, nameof(User), user.Id.ToString(), new { IsActive = false }, new { IsActive = true });
            await db.SaveChangesAsync(cancellationToken);
        }

        return ToResponse(user);
    }

    private Task<User?> LoadAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var users = tracking ? db.Users : db.Users.AsNoTracking();
        return users.Include(u => u.Roles).ThenInclude(r => r.Role).SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
    }

    private async Task<List<Guid>> ResolveRoleIdsAsync(IEnumerable<string> names, CancellationToken cancellationToken)
    {
        var wanted = names.Distinct().ToList();
        return await db.Roles.Where(r => wanted.Contains(r.Name)).Select(r => r.Id).ToListAsync(cancellationToken);
    }

    private Task<bool> IsLastActiveAdministratorAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users.AllAsync(u => u.Id == userId || !u.IsActive || !u.Roles.Any(r => r.RoleId == Role.AdministratorId), cancellationToken);

    private async Task RevokeSessionsAsync(Guid userId, string reason, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var tokens = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var token in tokens)
        {
            token.Revoke(now, reason);
        }
    }

    private static List<string> RoleNamesOf(User user) =>
        user.Roles.Where(r => r.Role is not null).Select(r => r.Role!.Name).Order(StringComparer.Ordinal).ToList();

    private static UserResponse ToResponse(User user) =>
        new(user.Id, user.Email, user.DisplayName, RoleNamesOf(user), user.IsActive, user.IsDemoData, user.CreatedAt, user.LastLoginAt);
}
