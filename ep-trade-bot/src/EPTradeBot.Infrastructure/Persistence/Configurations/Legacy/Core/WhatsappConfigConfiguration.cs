// Generated from OpenAlgo table 'whatsapp_config'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class WhatsappConfigConfiguration : IEntityTypeConfiguration<WhatsappConfig>
{
    public void Configure(EntityTypeBuilder<WhatsappConfig> b)
    {
        b.ToTable("whatsapp_config", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.SessionBlob).HasColumnName("session_blob");
        b.Property(x => x.OwnJid).HasColumnName("own_jid").HasMaxLength(120);
        b.Property(x => x.OwnPhone).HasColumnName("own_phone").HasMaxLength(32);
        b.Property(x => x.BotUsername).HasColumnName("bot_username").HasMaxLength(255);
        b.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
        b.Property(x => x.OwnerUsername).HasColumnName("owner_username").HasMaxLength(255);
        b.Property(x => x.IsPaired).HasColumnName("is_paired");
        b.Property(x => x.IsActive).HasColumnName("is_active");
        b.Property(x => x.PairedAt).HasColumnName("paired_at");
        b.Property(x => x.MaxMessageLength).HasColumnName("max_message_length");
        b.Property(x => x.RateLimitPerMinute).HasColumnName("rate_limit_per_minute");
        b.Property(x => x.BroadcastEnabled).HasColumnName("broadcast_enabled");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}
