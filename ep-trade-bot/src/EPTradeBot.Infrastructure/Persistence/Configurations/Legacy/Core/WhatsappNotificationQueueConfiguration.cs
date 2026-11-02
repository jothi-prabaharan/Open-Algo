// Generated from OpenAlgo table 'whatsapp_notification_queue'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class WhatsappNotificationQueueConfiguration : IEntityTypeConfiguration<WhatsappNotificationQueue>
{
    public void Configure(EntityTypeBuilder<WhatsappNotificationQueue> b)
    {
        b.ToTable("whatsapp_notification_queue", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.WhatsappJid).HasColumnName("whatsapp_jid").HasMaxLength(120).IsRequired();
        b.Property(x => x.Message).HasColumnName("message").IsRequired();
        b.Property(x => x.MediaPath).HasColumnName("media_path");
        b.Property(x => x.MediaKind).HasColumnName("media_kind").HasMaxLength(20);
        b.Property(x => x.Priority).HasColumnName("priority");
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.SentAt).HasColumnName("sent_at");
        b.Property(x => x.ErrorMessage).HasColumnName("error_message");
        b.HasIndex(x => x.Status).HasDatabaseName("ix_whatsapp_notification_queue_status");
        b.HasOne<WhatsappUser>().WithMany().HasPrincipalKey(p => p.WhatsappJid).HasForeignKey(x => x.WhatsappJid).OnDelete(DeleteBehavior.Cascade);
    }
}
