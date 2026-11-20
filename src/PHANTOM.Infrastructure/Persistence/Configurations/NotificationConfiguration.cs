using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PHANTOM.Domain.Identity;
using PHANTOM.Domain.Notifications;

namespace PHANTOM.Infrastructure.Persistence.Configurations;

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Title).HasMaxLength(Notification.MaxTitleLength).IsRequired();
        builder.Property(n => n.Body).HasMaxLength(Notification.MaxBodyLength).IsRequired();
        // Design §3.2: a user's unread list, newest first.
        builder.HasIndex(n => new { n.RecipientUserId, n.ReadAt, n.CreatedAt });
        builder.HasOne<User>().WithMany().HasForeignKey(n => n.RecipientUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
