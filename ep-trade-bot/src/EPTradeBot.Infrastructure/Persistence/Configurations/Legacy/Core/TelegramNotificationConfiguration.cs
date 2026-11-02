// Generated from OpenAlgo table 'notification_queue'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class TelegramNotificationConfiguration : IEntityTypeConfiguration<TelegramNotification>
{
    public void Configure(EntityTypeBuilder<TelegramNotification> b)
    {
        b.ToTable("notification_queue", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.TelegramId).HasColumnName("telegram_id").IsRequired();
        b.Property(x => x.Message).HasColumnName("message").IsRequired();
        b.Property(x => x.Priority).HasColumnName("priority");
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.SentAt).HasColumnName("sent_at");
        b.Property(x => x.ErrorMessage).HasColumnName("error_message");
        b.HasIndex(x => x.Status).HasDatabaseName("ix_notification_queue_status");
        b.HasOne<TelegramUser>().WithMany().HasPrincipalKey(p => p.TelegramId).HasForeignKey(x => x.TelegramId).OnDelete(DeleteBehavior.Cascade);
    }
}
