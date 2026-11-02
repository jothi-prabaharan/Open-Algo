// Generated from OpenAlgo table 'chart_preferences'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class ChartPreferenceConfiguration : IEntityTypeConfiguration<ChartPreference>
{
    public void Configure(EntityTypeBuilder<ChartPreference> b)
    {
        b.ToTable("chart_preferences", schema: "core");
        b.HasKey(x => new { x.UserId, x.Key });
        b.Property(x => x.UserId).HasColumnName("user_id").HasMaxLength(80).IsRequired();
        b.Property(x => x.Key).HasColumnName("key").HasMaxLength(50).IsRequired();
        b.Property(x => x.Value).HasColumnName("value");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}
