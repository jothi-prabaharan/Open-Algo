// Generated from OpenAlgo table 'leverage_config'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class LeverageConfigConfiguration : IEntityTypeConfiguration<LeverageConfig>
{
    public void Configure(EntityTypeBuilder<LeverageConfig> b)
    {
        b.ToTable("leverage_config", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Leverage).HasColumnName("leverage").IsRequired();
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}
