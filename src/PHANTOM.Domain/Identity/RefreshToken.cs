namespace PHANTOM.Domain.Identity;

/// <summary>
/// A hashed, single-use refresh token. Tokens issued by rotation share a <see cref="FamilyId"/>;
/// presenting an already-rotated token revokes the whole family (reuse detection).
/// </summary>
public sealed class RefreshToken
{
    private RefreshToken()
    {
        TokenHash = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; }
    public Guid FamilyId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevokedReason { get; private set; }
    public Guid? ReplacedById { get; private set; }

    public bool IsRevoked => RevokedAt.HasValue;

    public bool IsUsable(DateTimeOffset now) => !IsRevoked && now < ExpiresAt;

    public static RefreshToken Issue(Guid id, Guid userId, string tokenHash, Guid familyId, DateTimeOffset now, TimeSpan lifetime) =>
        new()
        {
            Id = id,
            UserId = userId,
            TokenHash = tokenHash,
            FamilyId = familyId,
            CreatedAt = now,
            ExpiresAt = now.Add(lifetime),
        };

    public void Revoke(DateTimeOffset now, string reason, Guid? replacedById = null)
    {
        if (IsRevoked)
        {
            return;
        }

        RevokedAt = now;
        RevokedReason = reason;
        ReplacedById = replacedById;
    }
}
