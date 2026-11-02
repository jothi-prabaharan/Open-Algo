// Generated from OpenAlgo table 'qty_freeze'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class QtyFreezeConfiguration : IEntityTypeConfiguration<QtyFreeze>
{
    public void Configure(EntityTypeBuilder<QtyFreeze> b)
    {
        b.ToTable("qty_freeze", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Exchange).HasColumnName("exchange").HasMaxLength(10).IsRequired();
        b.Property(x => x.Symbol).HasColumnName("symbol").HasMaxLength(50).IsRequired();
        b.Property(x => x.FreezeQty).HasColumnName("freeze_qty").IsRequired();
        b.HasIndex(x => x.Exchange).HasDatabaseName("ix_qty_freeze_exchange");
        b.HasIndex(x => new { x.Exchange, x.Symbol }).IsUnique().HasDatabaseName("ux_qty_freeze_exchange_symbol");
        b.HasIndex(x => x.Symbol).HasDatabaseName("ix_qty_freeze_symbol");
    }
}
