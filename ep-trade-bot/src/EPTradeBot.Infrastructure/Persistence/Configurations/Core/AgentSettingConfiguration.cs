using EPTradeBot.Core.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Core;

internal sealed class AgentSettingConfiguration : IEntityTypeConfiguration<AgentSetting>
{
    public void Configure(EntityTypeBuilder<AgentSetting> b)
    {
        b.ToTable("agent_settings", schema: Schemas.Core);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Endpoint).HasMaxLength(300).IsRequired();
        b.Property(x => x.Model).HasMaxLength(100).IsRequired();
        b.Property(x => x.ProtectedApiKey).HasMaxLength(2000);
        b.ToTable(t => t.HasCheckConstraint("ck_agent_settings_single_row", "id = 1"));
    }
}
