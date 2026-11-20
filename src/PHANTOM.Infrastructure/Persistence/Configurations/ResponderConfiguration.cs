using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PHANTOM.Domain.Identity;
using PHANTOM.Domain.Responders;

namespace PHANTOM.Infrastructure.Persistence.Configurations;

internal sealed class ResponderConfiguration : IEntityTypeConfiguration<Responder>
{
    public void Configure(EntityTypeBuilder<Responder> builder)
    {
        builder.ToTable("responders");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.CallSign).HasMaxLength(Responder.MaxCallSignLength).IsRequired();
        builder.HasIndex(r => r.CallSign).IsUnique();
        builder.Property(r => r.FullName).HasMaxLength(Responder.MaxNameLength).IsRequired();
        builder.Property(r => r.Team).HasMaxLength(Responder.MaxTeamLength).IsRequired();
        builder.Property(r => r.Phone).HasMaxLength(Responder.MaxPhoneLength);
        builder.Property(r => r.Email).HasMaxLength(Responder.MaxEmailLength);
        builder.Property(r => r.Skills).HasColumnType("text[]").IsRequired();
        builder.HasIndex(r => r.Skills).HasMethod("gin");
        builder.Property(r => r.Version).IsConcurrencyToken();
        builder.HasIndex(r => new { r.IsActive, r.Availability });
        builder.HasIndex(r => r.Team);

        // One responder profile per account.
        builder.HasIndex(r => r.UserId).IsUnique().HasFilter("user_id IS NOT NULL");
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
