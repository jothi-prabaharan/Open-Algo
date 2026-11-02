// Generated from OpenAlgo table 'sandbox_trades'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Sandbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Sandbox;

internal sealed class SandboxTradeConfiguration : IEntityTypeConfiguration<SandboxTrade>
{
    public void Configure(EntityTypeBuilder<SandboxTrade> b)
    {
        b.ToTable("sandbox_trades", schema: "sandbox");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Tradeid).HasColumnName("tradeid").HasMaxLength(50).IsRequired();
        b.Property(x => x.Orderid).HasColumnName("orderid").HasMaxLength(50).IsRequired();
        b.Property(x => x.UserId).HasColumnName("user_id").HasMaxLength(50).IsRequired();
        b.Property(x => x.Symbol).HasColumnName("symbol").HasMaxLength(50).IsRequired();
        b.Property(x => x.Exchange).HasColumnName("exchange").HasMaxLength(20).IsRequired();
        b.Property(x => x.Action).HasColumnName("action").HasMaxLength(10).IsRequired();
        b.Property(x => x.Quantity).HasColumnName("quantity").IsRequired();
        b.Property(x => x.Price).HasColumnName("price").HasPrecision(10, 2).IsRequired();
        b.Property(x => x.Product).HasColumnName("product").HasMaxLength(20).IsRequired();
        b.Property(x => x.Strategy).HasColumnName("strategy").HasMaxLength(100);
        b.Property(x => x.TradeTimestamp).HasColumnName("trade_timestamp").IsRequired();
        b.HasIndex(x => x.Tradeid).IsUnique().HasDatabaseName("ux_sandbox_trades_tradeid");
        b.HasIndex(x => x.Symbol).HasDatabaseName("ix_sandbox_trades_symbol");
        b.HasIndex(x => x.Orderid).HasDatabaseName("ix_sandbox_trades_orderid");
        b.HasIndex(x => x.Exchange).HasDatabaseName("ix_sandbox_trades_exchange");
        b.HasIndex(x => x.UserId).HasDatabaseName("ix_sandbox_trades_user_id");
        b.HasIndex(x => new { x.UserId, x.Symbol }).HasDatabaseName("ix_sandbox_trades_user_id_symbol");
    }
}
