// Generated from OpenAlgo table 'telegram_users'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class TelegramUserConfiguration : IEntityTypeConfiguration<TelegramUser>
{
    public void Configure(EntityTypeBuilder<TelegramUser> b)
    {
        b.ToTable("telegram_users", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.TelegramId).HasColumnName("telegram_id").IsRequired();
        b.Property(x => x.OpenalgoUsername).HasColumnName("openalgo_username").HasMaxLength(255).IsRequired();
        b.Property(x => x.EncryptedApiKey).HasColumnName("encrypted_api_key");
        b.Property(x => x.HostUrl).HasColumnName("host_url").HasMaxLength(500);
        b.Property(x => x.FirstName).HasColumnName("first_name").HasMaxLength(255);
        b.Property(x => x.LastName).HasColumnName("last_name").HasMaxLength(255);
        b.Property(x => x.TelegramUsername).HasColumnName("telegram_username").HasMaxLength(255);
        b.Property(x => x.Broker).HasColumnName("broker").HasMaxLength(50);
        b.Property(x => x.IsActive).HasColumnName("is_active");
        b.Property(x => x.NotificationsEnabled).HasColumnName("notifications_enabled");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.LastCommandAt).HasColumnName("last_command_at");
        b.HasIndex(x => x.TelegramId).IsUnique().HasDatabaseName("ux_telegram_users_telegram_id");
        b.HasIndex(x => x.OpenalgoUsername).HasDatabaseName("ix_telegram_users_openalgo_username");
    }
}
