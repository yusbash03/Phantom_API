namespace PHANTOM.Domain.Identity;

public sealed class Role
{
    public static readonly Guid AdministratorId = new("0193a000-0000-7000-8000-000000000001");
    public static readonly Guid IncidentCoordinatorId = new("0193a000-0000-7000-8000-000000000002");
    public static readonly Guid ResponderId = new("0193a000-0000-7000-8000-000000000003");

    private Role()
    {
        Name = string.Empty;
    }

    public Role(Guid id, string name)
    {
        Id = id;
        Name = name;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; }

    public static IReadOnlyList<Role> Seed =>
    [
        new(AdministratorId, RoleNames.Administrator),
        new(IncidentCoordinatorId, RoleNames.IncidentCoordinator),
        new(ResponderId, RoleNames.Responder),
    ];
}
