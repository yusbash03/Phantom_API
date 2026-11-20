using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PHANTOM.Domain.Incidents;

namespace PHANTOM.Infrastructure.Persistence.Configurations;

internal sealed class IncidentConfiguration : IEntityTypeConfiguration<Incident>
{
    public void Configure(EntityTypeBuilder<Incident> builder)
    {
        builder.ToTable("incidents");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Number).HasMaxLength(20).IsRequired();
        builder.HasIndex(i => i.Number).IsUnique();
        builder.Property(i => i.Title).HasMaxLength(Incident.MaxTitleLength).IsRequired();
        builder.Property(i => i.Description).HasMaxLength(Incident.MaxDescriptionLength).IsRequired();
        builder.Property(i => i.ResolutionSummary).HasMaxLength(Incident.MaxDescriptionLength);
        builder.Property(i => i.ClosureReason).HasMaxLength(Incident.MaxReasonLength);
        builder.Property(i => i.Version).IsConcurrencyToken();
        // Guards the geocoding hand-off without making routine geocoder writes conflict with coordinators.
        builder.Property(i => i.GeocodingGeneration).IsConcurrencyToken();

        // Owned Location value object (stored in-row, no separate table)
        builder.OwnsOne(i => i.Location, loc =>
        {
            loc.Property(l => l.Text).HasColumnName("location_text").HasMaxLength(Location.MaxTextLength).IsRequired();
            loc.Property(l => l.Latitude).HasColumnName("location_latitude").HasColumnType("numeric(9,6)");
            loc.Property(l => l.Longitude).HasColumnName("location_longitude").HasColumnType("numeric(9,6)");
            loc.Property(l => l.Provenance).HasColumnName("location_provenance").HasMaxLength(40).IsRequired();
            loc.Property(l => l.Precision).HasColumnName("location_precision").HasMaxLength(40).IsRequired();
            loc.Property(l => l.VerifiedById).HasColumnName("location_verified_by_id");
            loc.Property(l => l.VerifiedAt).HasColumnName("location_verified_at");
            loc.Property(l => l.GeocoderPlaceName).HasColumnName("location_geocoder_place_name").HasMaxLength(Location.MaxTextLength);
        });

        builder.HasIndex(i => new { i.Status, i.CreatedAt });
        builder.HasIndex(i => i.Category);
        builder.HasIndex(i => i.ApprovedSeverity);
        builder.HasIndex(i => i.CreatedAt);
        // Geocoding worker queue.
        builder.HasIndex(i => new { i.GeocodingStatus, i.CreatedAt });
    }
}

internal sealed class IncidentReportConfiguration : IEntityTypeConfiguration<IncidentReport>
{
    public void Configure(EntityTypeBuilder<IncidentReport> builder)
    {
        builder.ToTable("incident_reports");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Reference).HasMaxLength(IncidentReport.MaxReferenceLength).IsRequired();
        builder.HasIndex(r => r.Reference).IsUnique();
        builder.Property(r => r.Title).HasMaxLength(IncidentReport.MaxTitleLength).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(IncidentReport.MaxDescriptionLength).IsRequired();
        builder.Property(r => r.LocationText).HasMaxLength(IncidentReport.MaxLocationTextLength).IsRequired();
        builder.Property(r => r.ReporterName).HasMaxLength(IncidentReport.MaxNameLength);
        builder.Property(r => r.ReporterPhone).HasMaxLength(IncidentReport.MaxPhoneLength);
        builder.Property(r => r.ReporterEmail).HasMaxLength(IncidentReport.MaxEmailLength);
        builder.Property(r => r.ReportedLatitude).HasColumnType("numeric(9,6)");
        builder.Property(r => r.ReportedLongitude).HasColumnType("numeric(9,6)");
        builder.Property(r => r.ClientIpHash).HasMaxLength(64);

        // Idempotency: unique key within 24 h enforced at application layer; partial unique index for key
        builder.HasIndex(r => r.IdempotencyKey)
            .IsUnique()
            .HasFilter("idempotency_key IS NOT NULL");

        builder.HasOne<Incident>().WithMany().HasForeignKey(r => r.IncidentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.ToTable("attachments");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.StorageKey).HasMaxLength(Attachment.MaxStorageKeyLength).IsRequired();
        builder.HasIndex(a => a.StorageKey).IsUnique();
        builder.Property(a => a.ContentType).HasMaxLength(Attachment.MaxContentTypeLength).IsRequired();
        builder.Property(a => a.OriginalFileName).HasMaxLength(Attachment.MaxOriginalFileNameLength).IsRequired();
        builder.Property(a => a.Sha256).HasMaxLength(64).IsRequired();
        builder.Property(a => a.TranscriptText).HasMaxLength(Attachment.MaxTranscriptLength);
        builder.Property(a => a.TranscriptProvider).HasMaxLength(Attachment.MaxProviderLength);

        builder.HasOne<IncidentReport>().WithMany().HasForeignKey(a => a.ReportId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class IncidentAnalysisConfiguration : IEntityTypeConfiguration<IncidentAnalysis>
{
    public void Configure(EntityTypeBuilder<IncidentAnalysis> builder)
    {
        builder.ToTable("incident_analyses");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.SchemaVersion).HasMaxLength(IncidentAnalysis.MaxSchemaVersionLength).IsRequired();
        builder.Property(a => a.Provider).HasMaxLength(IncidentAnalysis.MaxProviderLength);
        builder.Property(a => a.Model).HasMaxLength(IncidentAnalysis.MaxModelLength);
        builder.Property(a => a.FailureMessage).HasMaxLength(IncidentAnalysis.MaxFailureMessageLength);
        builder.Property(a => a.Result).HasColumnType("jsonb");
        builder.Property(a => a.FailureCode).HasMaxLength(IncidentAnalysis.MaxFailureCodeLength);

        // Worker queue index
        builder.HasIndex(a => new { a.Status, a.RequestedAt });
        // Unique per (report, attempt)
        builder.HasIndex(a => new { a.ReportId, a.Attempt }).IsUnique();

        builder.HasOne<Incident>().WithMany().HasForeignKey(a => a.IncidentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<IncidentReport>().WithMany().HasForeignKey(a => a.ReportId).OnDelete(DeleteBehavior.Restrict);
    }
}
