// Generated from OpenAlgo table 'user_preferences'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class TelegramUserPreferenceConfiguration : IEntityTypeConfiguration<TelegramUserPreference>
{
    public void Configure(EntityTypeBuilder<TelegramUserPreference> b)
    {
        b.ToTable("user_preferences", schema: "core");
        b.HasKey(x => x.TelegramId);
        b.Property(x => x.TelegramId).ValueGeneratedOnAdd();
        b.Property(x => x.TelegramId).HasColumnName("telegram_id").IsRequired();
        b.Property(x => x.OrderNotifications).HasColumnName("order_notifications");
        b.Property(x => x.TradeNotifications).HasColumnName("trade_notifications");
        b.Property(x => x.PnlNotifications).HasColumnName("pnl_notifications");
        b.Property(x => x.DailySummary).HasColumnName("daily_summary");
        b.Property(x => x.SummaryTime).HasColumnName("summary_time").HasMaxLength(10);
        b.Property(x => x.Language).HasColumnName("language").HasMaxLength(10);
        b.Property(x => x.Timezone).HasColumnName("timezone").HasMaxLength(50);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.HasOne<TelegramUser>().WithMany().HasPrincipalKey(p => p.TelegramId).HasForeignKey(x => x.TelegramId).OnDelete(DeleteBehavior.Cascade);
    }
}
