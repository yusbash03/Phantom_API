using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PHANTOM.Application.Abstractions;
using PHANTOM.Application.Audit;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Common;
using PHANTOM.Domain.Identity;

namespace PHANTOM.Application.Auth;

public interface IAuthService
{
    Task<Result<AuthSession>> LoginAsync(LoginRequest request, CancellationToken cancellationToken);
    Task<Result<AuthSession>> RefreshAsync(string? rawRefreshToken, CancellationToken cancellationToken);
    Task LogoutAsync(string? rawRefreshToken, CancellationToken cancellationToken);
    Task<Result<CurrentUserResponse>> GetCurrentUserAsync(Guid userId, CancellationToken cancellationToken);
}

internal sealed class AuthService(
    IPhantomDbContext db,
    IPasswordHasher passwordHasher,
    ITokenService tokenService,
    IAuditWriter audit,
    IClock clock,
    IOptions<AuthOptions> options,
    ILogger<AuthService> logger) : IAuthService
{
    // Deliberately identical for unknown email, wrong password and inactive account (no enumeration).
    private static readonly Error InvalidCredentials = Error.Unauthorized("auth.invalid_credentials", "The email or password is incorrect.");
    private static readonly Error InvalidSession = Error.Unauthorized("auth.invalid_session", "Your session has expired. Please sign in again.");

    public async Task<Result<AuthSession>> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var email = User.NormalizeEmail(request.Email ?? string.Empty);
        var password = request.Password ?? string.Empty;

        // Read-only: sign-in must not take part in optimistic concurrency on the user row, otherwise two
        // simultaneous sign-ins conflict, and a stale copy could overwrite a concurrent deactivation.
        var user = await db.Users.AsNoTracking()
            .Include(u => u.Roles).ThenInclude(r => r.Role)
            .SingleOrDefaultAsync(u => u.Email == email, cancellationToken);

        if (user is null)
        {
            passwordHasher.SimulateVerification(password);
            return await FailLoginAsync(null, "unknown_account", cancellationToken);
        }

        if (await IsThrottledAsync(user.Id, cancellationToken))
        {
            passwordHasher.SimulateVerification(password);
            return await FailLoginAsync(user.Id, "throttled", cancellationToken);
        }

        var check = passwordHasher.Verify(user, user.PasswordHash, password);
        if (check == PasswordCheck.Failed)
        {
            return await FailLoginAsync(user.Id, "wrong_password", cancellationToken);
        }

        if (!user.IsActive)
        {
            return await FailLoginAsync(user.Id, "inactive_account", cancellationToken);
        }

        var now = clock.UtcNow;
        var session = IssueSession(user, Guid.CreateVersion7(), now);
        audit.Record(AuditActions.LoginSucceeded, nameof(User), user.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);

        // Targeted column updates: last-write-wins is correct for a timestamp and touches nothing else.
        await db.Users.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.LastLoginAt, now), cancellationToken);
        if (check == PasswordCheck.SuccessRehashNeeded)
        {
            var upgradedHash = passwordHasher.Hash(user, password);
            await db.Users.Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.PasswordHash, upgradedHash), cancellationToken);
        }

        return session;
    }

    public async Task<Result<AuthSession>> RefreshAsync(string? rawRefreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            return InvalidSession;
        }

        var hash = tokenService.HashRefreshToken(rawRefreshToken);
        var existing = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (existing is null)
        {
            return InvalidSession;
        }

        var now = clock.UtcNow;
        if (existing.IsRevoked)
        {
            // A rotated token was presented again: assume theft and revoke the whole family.
            await RevokeFamilyAsync(existing.FamilyId, now, "reuse_detected", cancellationToken);
            audit.RecordSystem(AuditActions.RefreshTokenReuseDetected, nameof(User), existing.UserId.ToString(), metadata: new { existing.FamilyId });
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Refresh token reuse detected for token family {FamilyId}; family revoked", existing.FamilyId);
            return InvalidSession;
        }

        if (!existing.IsUsable(now))
        {
            return InvalidSession;
        }

        // Roles are re-read from the database, never carried over from the old token.
        var user = await db.Users
            .Include(u => u.Roles).ThenInclude(r => r.Role)
            .SingleOrDefaultAsync(u => u.Id == existing.UserId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            await RevokeFamilyAsync(existing.FamilyId, now, "account_inactive", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return InvalidSession;
        }

        var session = IssueSession(user, existing.FamilyId, now, out var replacement);
        existing.Revoke(now, "rotated", replacement.Id);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request rotated this token at the same moment; only one may win.
            return InvalidSession;
        }

        return session;
    }

    public async Task LogoutAsync(string? rawRefreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            return;
        }

        var hash = tokenService.HashRefreshToken(rawRefreshToken);
        var existing = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (existing is null)
        {
            return;
        }

        await RevokeFamilyAsync(existing.FamilyId, clock.UtcNow, "logout", cancellationToken);
        audit.Record(AuditActions.Logout, nameof(User), existing.UserId.ToString());
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Result<CurrentUserResponse>> GetCurrentUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking()
            .Include(u => u.Roles).ThenInclude(r => r.Role)
            .SingleOrDefaultAsync(u => u.Id == userId && u.IsActive, cancellationToken);
        return user is null ? InvalidSession : ToResponse(user);
    }

    private AuthSession IssueSession(User user, Guid familyId, DateTimeOffset now) => IssueSession(user, familyId, now, out _);

    private AuthSession IssueSession(User user, Guid familyId, DateTimeOffset now, out RefreshToken refreshToken)
    {
        var response = ToResponse(user);
        var access = tokenService.CreateAccessToken(user, response.Roles.ToList());
        var generated = tokenService.GenerateRefreshToken();
        refreshToken = RefreshToken.Issue(Guid.CreateVersion7(), user.Id, generated.Hash, familyId, now,
            TimeSpan.FromDays(options.Value.RefreshTokenLifetimeDays));
        db.RefreshTokens.Add(refreshToken);
        return new AuthSession(access.Token, access.ExpiresAt, generated.RawToken, refreshToken.ExpiresAt, response);
    }

    private async Task<Result<AuthSession>> FailLoginAsync(Guid? userId, string reason, CancellationToken cancellationToken)
    {
        audit.Record(AuditActions.LoginFailed, nameof(User), userId?.ToString(), metadata: new { reason });
        await db.SaveChangesAsync(cancellationToken);
        return InvalidCredentials;
    }

    private async Task<bool> IsThrottledAsync(Guid userId, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var since = clock.UtcNow.AddMinutes(-settings.FailedLoginWindowMinutes);
        var entityId = userId.ToString();
        var failures = await db.AuditLogs.CountAsync(
            a => a.Action == AuditActions.LoginFailed && a.EntityId == entityId && a.OccurredAt >= since, cancellationToken);
        return failures >= settings.MaxFailedLoginsPerAccount;
    }

    private async Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, string reason, CancellationToken cancellationToken)
    {
        var tokens = await db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var token in tokens)
        {
            token.Revoke(now, reason);
        }
    }

    internal static CurrentUserResponse ToResponse(User user) =>
        new(user.Id, user.Email, user.DisplayName,
            user.Roles.Where(r => r.Role is not null).Select(r => r.Role!.Name).Order(StringComparer.Ordinal).ToList());
}
