// Generated from OpenAlgo table 'master_contract_status'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class MasterContractStatusConfiguration : IEntityTypeConfiguration<MasterContractStatus>
{
    public void Configure(EntityTypeBuilder<MasterContractStatus> b)
    {
        b.ToTable("master_contract_status", schema: "core");
        b.HasKey(x => x.Broker);
        b.Property(x => x.Broker).ValueGeneratedNever();
        b.Property(x => x.Broker).HasColumnName("broker").IsRequired();
        b.Property(x => x.Status).HasColumnName("status");
        b.Property(x => x.Message).HasColumnName("message");
        b.Property(x => x.LastUpdated).HasColumnName("last_updated");
        b.Property(x => x.TotalSymbols).HasColumnName("total_symbols");
        b.Property(x => x.IsReady).HasColumnName("is_ready");
        b.Property(x => x.LastDownloadTime).HasColumnName("last_download_time");
        b.Property(x => x.DownloadDate).HasColumnName("download_date");
        b.Property(x => x.ExchangeStats).HasColumnName("exchange_stats");
        b.Property(x => x.DownloadDurationSeconds).HasColumnName("download_duration_seconds");
    }
}
