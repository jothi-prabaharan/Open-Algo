// Generated from OpenAlgo table 'sandbox_config'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Sandbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Sandbox;

internal sealed class SandboxConfigConfiguration : IEntityTypeConfiguration<SandboxConfig>
{
    public void Configure(EntityTypeBuilder<SandboxConfig> b)
    {
        b.ToTable("sandbox_config", schema: "sandbox");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.ConfigKey).HasColumnName("config_key").HasMaxLength(100).IsRequired();
        b.Property(x => x.ConfigValue).HasColumnName("config_value").IsRequired();
        b.Property(x => x.Description).HasColumnName("description");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        b.HasIndex(x => x.ConfigKey).IsUnique().HasDatabaseName("ux_sandbox_config_config_key");
    }
}
