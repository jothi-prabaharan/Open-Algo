// Generated from OpenAlgo table 'ag_setting'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class AgSettingConfiguration : IEntityTypeConfiguration<AgSetting>
{
    public void Configure(EntityTypeBuilder<AgSetting> b)
    {
        b.ToTable("ag_setting", schema: "core");
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).ValueGeneratedNever();
        b.Property(x => x.Key).HasColumnName("key").HasMaxLength(120).IsRequired();
        b.Property(x => x.Value).HasColumnName("value");
    }
}
