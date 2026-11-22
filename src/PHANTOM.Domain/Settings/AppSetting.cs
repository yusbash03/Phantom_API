namespace PHANTOM.Domain.Settings;

/// <summary>
/// An administrator-adjustable operational value (Requirement 17; design §3.2). Stored as JSON under a key.
/// Never holds secrets: those stay in environment configuration.
/// </summary>
public sealed class AppSetting
{
    public const int MaxKeyLength = 100;

    private AppSetting()
    {
        // EF Core
        Key = string.Empty;
        Value = string.Empty;
    }

    public string Key { get; private set; }
    public string Value { get; private set; }
    public Guid? UpdatedById { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static AppSetting Create(string key, string value, Guid? updatedById, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new AppSetting { Key = key, Value = value, UpdatedById = updatedById, UpdatedAt = now };
    }

    public void Update(string value, Guid? updatedById, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
        UpdatedById = updatedById;
        UpdatedAt = now;
    }
}
