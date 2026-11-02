// Generated from OpenAlgo table 'api_keys'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> b)
    {
        b.ToTable("api_keys", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        b.Property(x => x.ApiKeyHash).HasColumnName("api_key_hash").IsRequired();
        b.Property(x => x.ApiKeyEncrypted).HasColumnName("api_key_encrypted").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.OrderMode).HasColumnName("order_mode").HasMaxLength(20);
        b.HasIndex(x => x.UserId).IsUnique().HasDatabaseName("ux_api_keys_user_id");
        b.HasIndex(x => x.OrderMode).HasDatabaseName("ix_api_keys_order_mode");
        b.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_api_keys_created_at");
    }
}
