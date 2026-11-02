// Generated from OpenAlgo table 'bot_config'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class TelegramBotConfigConfiguration : IEntityTypeConfiguration<TelegramBotConfig>
{
    public void Configure(EntityTypeBuilder<TelegramBotConfig> b)
    {
        b.ToTable("bot_config", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Token).HasColumnName("token");
        b.Property(x => x.IsActive).HasColumnName("is_active");
        b.Property(x => x.BotUsername).HasColumnName("bot_username").HasMaxLength(255);
        b.Property(x => x.MaxMessageLength).HasColumnName("max_message_length");
        b.Property(x => x.RateLimitPerMinute).HasColumnName("rate_limit_per_minute");
        b.Property(x => x.BroadcastEnabled).HasColumnName("broadcast_enabled");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}
