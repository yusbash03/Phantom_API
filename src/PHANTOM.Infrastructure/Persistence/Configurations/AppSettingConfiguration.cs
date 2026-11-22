using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PHANTOM.Domain.Identity;
using PHANTOM.Domain.Settings;

namespace PHANTOM.Infrastructure.Persistence.Configurations;

internal sealed class AppSettingConfiguration : IEntityTypeConfiguration<AppSetting>
{
    public void Configure(EntityTypeBuilder<AppSetting> builder)
    {
        builder.ToTable("app_settings");
        builder.HasKey(s => s.Key);
        builder.Property(s => s.Key).HasMaxLength(AppSetting.MaxKeyLength);
        builder.Property(s => s.Value).HasColumnType("jsonb").IsRequired();
        builder.HasOne<User>().WithMany().HasForeignKey(s => s.UpdatedById).OnDelete(DeleteBehavior.Restrict);
    }
}
