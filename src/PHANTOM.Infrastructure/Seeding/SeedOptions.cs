namespace PHANTOM.Infrastructure.Seeding;

public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    /// <summary>Seed demonstration accounts and data. Ignored in Production unless <see cref="AllowDemoInProduction"/>.</summary>
    public bool Enabled { get; set; }

    /// <summary>Explicit opt-in for the public judging deployment only. Demo records are always flagged <c>IsDemoData</c>.</summary>
    public bool AllowDemoInProduction { get; set; }

    /// <summary>Password for the demo accounts. Supplied by configuration, never hard-coded.</summary>
    public string? DemoUserPassword { get; set; }
}
