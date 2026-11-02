// Generated from OpenAlgo table 'settings'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class AppSettingConfiguration : IEntityTypeConfiguration<AppSetting>
{
    public void Configure(EntityTypeBuilder<AppSetting> b)
    {
        b.ToTable("settings", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.AnalyzeMode).HasColumnName("analyze_mode");
        b.Property(x => x.SmtpServer).HasColumnName("smtp_server").HasMaxLength(255);
        b.Property(x => x.SmtpPort).HasColumnName("smtp_port");
        b.Property(x => x.SmtpUsername).HasColumnName("smtp_username").HasMaxLength(255);
        b.Property(x => x.SmtpPasswordEncrypted).HasColumnName("smtp_password_encrypted");
        b.Property(x => x.SmtpUseTls).HasColumnName("smtp_use_tls");
        b.Property(x => x.SmtpFromEmail).HasColumnName("smtp_from_email").HasMaxLength(255);
        b.Property(x => x.SmtpHeloHostname).HasColumnName("smtp_helo_hostname").HasMaxLength(255);
        b.Property(x => x.SecurityAutoBanEnabled).HasColumnName("security_auto_ban_enabled");
        b.Property(x => x.Security404Threshold).HasColumnName("security_404_threshold");
        b.Property(x => x.Security404BanDuration).HasColumnName("security_404_ban_duration");
        b.Property(x => x.SecurityApiThreshold).HasColumnName("security_api_threshold");
        b.Property(x => x.SecurityApiBanDuration).HasColumnName("security_api_ban_duration");
        b.Property(x => x.SecurityRepeatOffenderLimit).HasColumnName("security_repeat_offender_limit");
    }
}
