// Generated from OpenAlgo table 'command_logs'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Audit;

internal sealed class TelegramCommandLogConfiguration : IEntityTypeConfiguration<TelegramCommandLog>
{
    public void Configure(EntityTypeBuilder<TelegramCommandLog> b)
    {
        b.ToTable("command_logs", schema: "audit");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.TelegramId).HasColumnName("telegram_id").IsRequired();
        b.Property(x => x.Command).HasColumnName("command").HasMaxLength(100).IsRequired();
        b.Property(x => x.ChatId).HasColumnName("chat_id");
        b.Property(x => x.Parameters).HasColumnName("parameters");
        b.Property(x => x.ExecutedAt).HasColumnName("executed_at");
        b.HasIndex(x => x.TelegramId).HasDatabaseName("ix_command_logs_telegram_id");
        b.HasOne<EPTradeBot.Core.Entities.Legacy.Core.TelegramUser>().WithMany().HasPrincipalKey(p => p.TelegramId).HasForeignKey(x => x.TelegramId).OnDelete(DeleteBehavior.Cascade);
    }
}
