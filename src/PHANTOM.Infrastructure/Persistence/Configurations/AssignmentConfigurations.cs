using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PHANTOM.Domain.Assignments;
using PHANTOM.Domain.Identity;
using PHANTOM.Domain.Incidents;
using PHANTOM.Domain.Responders;

namespace PHANTOM.Infrastructure.Persistence.Configurations;

internal sealed class ResponderAssignmentConfiguration : IEntityTypeConfiguration<ResponderAssignment>
{
    public void Configure(EntityTypeBuilder<ResponderAssignment> builder)
    {
        builder.ToTable("responder_assignments");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.EndedReason).HasMaxLength(ResponderAssignment.MaxReasonLength);
        builder.Property(a => a.OverrideReason).HasMaxLength(ResponderAssignment.MaxReasonLength);
        builder.Property(a => a.CompletionNotes).HasMaxLength(ResponderAssignment.MaxNotesLength);
        builder.Property(a => a.Version).IsConcurrencyToken();

        // Design §3.2: at most one non-terminal assignment per (incident, responder).
        builder.HasIndex(a => new { a.IncidentId, a.ResponderId })
            .IsUnique()
            .HasFilter("status NOT IN ('Completed', 'Declined', 'Cancelled')")
            .HasDatabaseName("ux_responder_assignments_active_pair");
        builder.HasIndex(a => new { a.ResponderId, a.Status });
        builder.HasIndex(a => new { a.IncidentId, a.AssignedAt });

        builder.HasOne<Incident>().WithMany().HasForeignKey(a => a.IncidentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Responder>().WithMany().HasForeignKey(a => a.ResponderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(a => a.AssignedById).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class IncidentUpdateConfiguration : IEntityTypeConfiguration<IncidentUpdate>
{
    public void Configure(EntityTypeBuilder<IncidentUpdate> builder)
    {
        builder.ToTable("incident_updates");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Body).HasMaxLength(IncidentUpdate.MaxBodyLength).IsRequired();
        builder.HasIndex(u => new { u.IncidentId, u.CreatedAt });
        builder.HasIndex(u => u.AssignmentId);

        builder.HasOne<Incident>().WithMany().HasForeignKey(u => u.IncidentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ResponderAssignment>().WithMany().HasForeignKey(u => u.AssignmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(u => u.AuthorId).OnDelete(DeleteBehavior.Restrict);
    }
}
