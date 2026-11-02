// Generated from OpenAlgo table 'whatsapp_user_preferences'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class WhatsappUserPreferenceConfiguration : IEntityTypeConfiguration<WhatsappUserPreference>
{
    public void Configure(EntityTypeBuilder<WhatsappUserPreference> b)
    {
        b.ToTable("whatsapp_user_preferences", schema: "core");
        b.HasKey(x => x.WhatsappJid);
        b.Property(x => x.WhatsappJid).ValueGeneratedNever();
        b.Property(x => x.WhatsappJid).HasColumnName("whatsapp_jid").HasMaxLength(120).IsRequired();
        b.Property(x => x.OrderNotifications).HasColumnName("order_notifications");
        b.Property(x => x.TradeNotifications).HasColumnName("trade_notifications");
        b.Property(x => x.PnlNotifications).HasColumnName("pnl_notifications");
        b.Property(x => x.DailySummary).HasColumnName("daily_summary");
        b.Property(x => x.SummaryTime).HasColumnName("summary_time").HasMaxLength(10);
        b.Property(x => x.Language).HasColumnName("language").HasMaxLength(10);
        b.Property(x => x.Timezone).HasColumnName("timezone").HasMaxLength(50);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.HasOne<WhatsappUser>().WithMany().HasPrincipalKey(p => p.WhatsappJid).HasForeignKey(x => x.WhatsappJid).OnDelete(DeleteBehavior.Cascade);
    }
}
