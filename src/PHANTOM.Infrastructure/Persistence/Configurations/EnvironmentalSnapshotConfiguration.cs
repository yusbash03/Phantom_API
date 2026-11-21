using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PHANTOM.Domain.Incidents;
using PHANTOM.Domain.Weather;

namespace PHANTOM.Infrastructure.Persistence.Configurations;

internal sealed class EnvironmentalSnapshotConfiguration : IEntityTypeConfiguration<EnvironmentalSnapshot>
{
    public void Configure(EntityTypeBuilder<EnvironmentalSnapshot> builder)
    {
        builder.ToTable("environmental_snapshots");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Provider).HasMaxLength(EnvironmentalSnapshot.MaxProviderLength).IsRequired();
        builder.Property(s => s.Latitude).HasPrecision(9, 6);
        builder.Property(s => s.Longitude).HasPrecision(9, 6);
        builder.Property(s => s.Data).HasColumnType("jsonb");
        builder.Property(s => s.FailureMessage).HasMaxLength(EnvironmentalSnapshot.MaxMessageLength);
        builder.HasIndex(s => new { s.IncidentId, s.CreatedAt });
        builder.HasOne<Incident>().WithMany().HasForeignKey(s => s.IncidentId).OnDelete(DeleteBehavior.Restrict);
    }
}
