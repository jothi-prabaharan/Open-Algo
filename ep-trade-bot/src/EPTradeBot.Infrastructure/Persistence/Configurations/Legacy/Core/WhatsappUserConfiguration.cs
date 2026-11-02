// Generated from OpenAlgo table 'whatsapp_users'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class WhatsappUserConfiguration : IEntityTypeConfiguration<WhatsappUser>
{
    public void Configure(EntityTypeBuilder<WhatsappUser> b)
    {
        b.ToTable("whatsapp_users", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.WhatsappJid).HasColumnName("whatsapp_jid").HasMaxLength(120).IsRequired();
        b.Property(x => x.PhoneNumber).HasColumnName("phone_number").HasMaxLength(32).IsRequired();
        b.Property(x => x.OpenalgoUsername).HasColumnName("openalgo_username").HasMaxLength(255).IsRequired();
        b.Property(x => x.EncryptedApiKey).HasColumnName("encrypted_api_key");
        b.Property(x => x.HostUrl).HasColumnName("host_url").HasMaxLength(500);
        b.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(255);
        b.Property(x => x.Broker).HasColumnName("broker").HasMaxLength(50);
        b.Property(x => x.IsActive).HasColumnName("is_active");
        b.Property(x => x.NotificationsEnabled).HasColumnName("notifications_enabled");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.LastCommandAt).HasColumnName("last_command_at");
        b.HasIndex(x => x.WhatsappJid).IsUnique().HasDatabaseName("ux_whatsapp_users_whatsapp_jid");
        b.HasIndex(x => x.OpenalgoUsername).HasDatabaseName("ix_whatsapp_users_openalgo_username");
        b.HasIndex(x => x.PhoneNumber).HasDatabaseName("ix_whatsapp_users_phone_number");
    }
}
