using EPTradeBot.Core.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Core;

internal sealed class AccountRiskSettingConfiguration : IEntityTypeConfiguration<AccountRiskSetting>
{
    public void Configure(EntityTypeBuilder<AccountRiskSetting> b)
    {
        // No CHECK constraints: they take SQL text, which the zero-raw-SQL rule forbids.
        // Ranges are enforced by Data Annotations on the request DTO and on the entity.
        b.ToTable("account_risk_settings", schema: Schemas.Core);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.MaxDailyLoss).HasPrecision(18, 2);
        b.Property(x => x.MaxPositionValue).HasPrecision(18, 2);
        b.Property(x => x.UpdatedBy).HasMaxLength(80);
        b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        b.HasIndex(x => x.AccountId).IsUnique().HasDatabaseName("ux_account_risk_settings_account_id");
    }
}
