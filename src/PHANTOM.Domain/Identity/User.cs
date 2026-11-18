namespace PHANTOM.Domain.Identity;

public sealed class User
{
    public const int MaxEmailLength = 256;
    public const int MaxDisplayNameLength = 120;
    public const int MinPasswordLength = 12;

    private readonly List<UserRole> _roles = [];

    private User()
    {
        Email = string.Empty;
        DisplayName = string.Empty;
        PasswordHash = string.Empty;
    }

    public Guid Id { get; private set; }
    public string Email { get; private set; }
    public string DisplayName { get; private set; }
    public string PasswordHash { get; private set; }
    public bool IsActive { get; private set; }
    public bool IsDemoData { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }
    public uint Version { get; private set; }
    public IReadOnlyCollection<UserRole> Roles => _roles;

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static User Create(Guid id, string email, string displayName, DateTimeOffset now, bool isDemoData = false) =>
        new()
        {
            Id = id,
            Email = NormalizeEmail(email),
            DisplayName = displayName.Trim(),
            IsActive = true,
            IsDemoData = isDemoData,
            CreatedAt = now,
        };

    public void SetPasswordHash(string hash) => PasswordHash = hash;

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;

    public void SetDisplayName(string displayName) => DisplayName = displayName.Trim();

    public bool HasRole(Guid roleId) => _roles.Any(r => r.RoleId == roleId);

    public void SetRoles(IEnumerable<Guid> roleIds)
    {
        var target = roleIds.Distinct().ToHashSet();
        _roles.RemoveAll(r => !target.Contains(r.RoleId));
        foreach (var roleId in target.Where(id => !HasRole(id)))
        {
            _roles.Add(new UserRole(Id, roleId));
        }
    }
}
