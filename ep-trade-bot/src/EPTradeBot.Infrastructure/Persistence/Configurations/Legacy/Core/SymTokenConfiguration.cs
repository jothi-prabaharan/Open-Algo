// Generated from OpenAlgo table 'symtoken'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class SymTokenConfiguration : IEntityTypeConfiguration<SymToken>
{
    public void Configure(EntityTypeBuilder<SymToken> b)
    {
        b.ToTable("symtoken", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Symbol).HasColumnName("symbol").IsRequired();
        b.Property(x => x.Brsymbol).HasColumnName("brsymbol").IsRequired();
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.Exchange).HasColumnName("exchange");
        b.Property(x => x.Brexchange).HasColumnName("brexchange");
        b.Property(x => x.Token).HasColumnName("token");
        b.Property(x => x.Expiry).HasColumnName("expiry");
        b.Property(x => x.Strike).HasColumnName("strike");
        b.Property(x => x.Lotsize).HasColumnName("lotsize");
        b.Property(x => x.Instrumenttype).HasColumnName("instrumenttype");
        b.Property(x => x.TickSize).HasColumnName("tick_size");
        b.Property(x => x.ContractValue).HasColumnName("contract_value");
        b.HasIndex(x => x.Brsymbol).HasDatabaseName("ix_symtoken_brsymbol");
        b.HasIndex(x => x.Token).HasDatabaseName("ix_symtoken_token");
        b.HasIndex(x => new { x.Brsymbol, x.Exchange }).HasDatabaseName("ix_symtoken_brsymbol_exchange");
        b.HasIndex(x => x.Symbol).HasDatabaseName("ix_symtoken_symbol");
        b.HasIndex(x => x.Exchange).HasDatabaseName("ix_symtoken_exchange");
        b.HasIndex(x => new { x.Symbol, x.Name }).HasDatabaseName("ix_symtoken_symbol_name");
        b.HasIndex(x => x.Brexchange).HasDatabaseName("ix_symtoken_brexchange");
        b.HasIndex(x => new { x.Symbol, x.Exchange }).HasDatabaseName("ix_symtoken_symbol_exchange");
    }
}
