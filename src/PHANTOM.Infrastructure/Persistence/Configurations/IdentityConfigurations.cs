using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PHANTOM.Domain.Identity;

namespace PHANTOM.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Email).HasMaxLength(User.MaxEmailLength).IsRequired();
        builder.HasIndex(u => u.Email).IsUnique();
        builder.Property(u => u.DisplayName).HasMaxLength(User.MaxDisplayNameLength).IsRequired();
        builder.Property(u => u.PasswordHash).HasMaxLength(512).IsRequired();
        builder.Property(u => u.Version).IsRowVersion();
        builder.HasMany(u => u.Roles).WithOne().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(u => u.Roles).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Name).HasMaxLength(64).IsRequired();
        builder.HasIndex(r => r.Name).IsUnique();
        builder.HasData(Role.Seed);
    }
}

internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        builder.ToTable("user_roles");
        builder.HasKey(r => new { r.UserId, r.RoleId });
        builder.HasOne(r => r.Role).WithMany().HasForeignKey(r => r.RoleId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.TokenHash).HasMaxLength(128).IsRequired();
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => t.FamilyId);
        builder.HasIndex(t => t.UserId);
        builder.Property(t => t.RevokedReason).HasMaxLength(64);
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        // Optimistic concurrency on PostgreSQL xmin: two simultaneous rotations of one token cannot both succeed.
        builder.Property<uint>("Version").IsRowVersion();
    }
}
