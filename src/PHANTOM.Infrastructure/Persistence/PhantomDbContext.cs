using Microsoft.EntityFrameworkCore;
using PHANTOM.Application.Abstractions;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Identity;

namespace PHANTOM.Infrastructure.Persistence;

public sealed class PhantomDbContext(DbContextOptions<PhantomDbContext> options) : DbContext(options), IPhantomDbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PhantomDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        // Enums are stored as readable strings, never as ordinals that break when reordered.
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(40);
    }
}
