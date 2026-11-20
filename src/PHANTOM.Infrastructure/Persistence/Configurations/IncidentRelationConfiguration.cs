using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PHANTOM.Domain.Correlation;
using PHANTOM.Domain.Incidents;

namespace PHANTOM.Infrastructure.Persistence.Configurations;

internal sealed class IncidentRelationConfiguration : IEntityTypeConfiguration<IncidentRelation>
{
    public void Configure(EntityTypeBuilder<IncidentRelation> builder)
    {
        builder.ToTable("incident_relations");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Score).HasColumnType("numeric(4,3)");
        builder.Property(r => r.Signals).HasColumnType("jsonb");
        builder.Property(r => r.Explanation).HasMaxLength(IncidentRelation.MaxExplanationLength);

        // Pairs are stored ordered by the domain, so this makes each pair unique and symmetric.
        builder.HasIndex(r => new { r.SourceIncidentId, r.TargetIncidentId }).IsUnique();
        builder.HasIndex(r => r.TargetIncidentId);
        builder.HasIndex(r => r.Type);

        // Restrict: correlation never deletes incidents, and incidents are never deleted under a relation.
        builder.HasOne<Incident>().WithMany().HasForeignKey(r => r.SourceIncidentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Incident>().WithMany().HasForeignKey(r => r.TargetIncidentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Incident>().WithMany().HasForeignKey(r => r.PrimaryIncidentId).OnDelete(DeleteBehavior.Restrict);
    }
}
