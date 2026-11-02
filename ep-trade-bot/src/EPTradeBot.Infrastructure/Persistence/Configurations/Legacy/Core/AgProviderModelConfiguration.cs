// Generated from OpenAlgo table 'ag_provider_model'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class AgProviderModelConfiguration : IEntityTypeConfiguration<AgProviderModel>
{
    public void Configure(EntityTypeBuilder<AgProviderModel> b)
    {
        b.ToTable("ag_provider_model", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.ProviderKind).HasColumnName("provider_kind").HasMaxLength(32).IsRequired();
        b.Property(x => x.ModelName).HasColumnName("model_name").HasMaxLength(200).IsRequired();
        b.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
        b.Property(x => x.BaseUrl).HasColumnName("base_url").HasMaxLength(500);
        b.Property(x => x.Enabled).HasColumnName("enabled").IsRequired();
        b.Property(x => x.IsDefault).HasColumnName("is_default").IsRequired();
        b.Property(x => x.SupportsReasoning).HasColumnName("supports_reasoning").IsRequired();
        b.Property(x => x.DefaultReasoningEffort).HasColumnName("default_reasoning_effort").HasMaxLength(16).IsRequired();
        b.Property(x => x.SupportsVision).HasColumnName("supports_vision").IsRequired();
        b.Property(x => x.ToolsUnreliable).HasColumnName("tools_unreliable").IsRequired();
        b.Property(x => x.LastTestedAt).HasColumnName("last_tested_at");
        b.Property(x => x.LastTestOk).HasColumnName("last_test_ok");
        b.Property(x => x.LastTestError).HasColumnName("last_test_error");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        b.HasIndex(x => new { x.ProviderKind, x.ModelName, x.BaseUrl }).IsUnique().HasDatabaseName("ux_ag_provider_model_provider_kind_model_name_base_url");
        b.HasIndex(x => x.Enabled).HasDatabaseName("ix_ag_provider_model_enabled");
    }
}
