// Generated from OpenAlgo table 'strategy_portfolio'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class StrategyPortfolioEntryConfiguration : IEntityTypeConfiguration<StrategyPortfolioEntry>
{
    public void Configure(EntityTypeBuilder<StrategyPortfolioEntry> b)
    {
        b.ToTable("strategy_portfolio", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Watchlist).HasColumnName("watchlist").HasMaxLength(20).IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(120).IsRequired();
        b.Property(x => x.Underlying).HasColumnName("underlying").HasMaxLength(40).IsRequired();
        b.Property(x => x.Exchange).HasColumnName("exchange").HasMaxLength(20).IsRequired();
        b.Property(x => x.Expiry).HasColumnName("expiry").HasMaxLength(20);
        b.Property(x => x.LegsJson).HasColumnName("legs_json").IsRequired();
        b.Property(x => x.Notes).HasColumnName("notes");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        b.HasIndex(x => x.Watchlist).HasDatabaseName("ix_strategy_portfolio_watchlist");
    }
}
