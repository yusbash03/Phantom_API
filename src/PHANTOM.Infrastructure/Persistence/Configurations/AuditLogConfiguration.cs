using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PHANTOM.Domain.Audit;

namespace PHANTOM.Infrastructure.Persistence.Configurations;

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).UseIdentityAlwaysColumn();
        builder.Property(a => a.Action).HasMaxLength(100).IsRequired();
        builder.Property(a => a.EntityType).HasMaxLength(100).IsRequired();
        builder.Property(a => a.EntityId).HasMaxLength(100);
        builder.Property(a => a.CorrelationId).HasMaxLength(100);
        builder.Property(a => a.PreviousValue).HasColumnType("jsonb");
        builder.Property(a => a.NewValue).HasColumnType("jsonb");
        builder.Property(a => a.Metadata).HasColumnType("jsonb");
        builder.HasIndex(a => new { a.EntityType, a.EntityId, a.OccurredAt });
        builder.HasIndex(a => new { a.ActorUserId, a.OccurredAt });
        builder.HasIndex(a => a.Action);
        builder.HasIndex(a => a.OccurredAt);
    }
}
