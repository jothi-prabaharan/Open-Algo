// Generated from OpenAlgo table 'analyzer_logs'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Audit;

internal sealed class AnalyzerLogConfiguration : IEntityTypeConfiguration<AnalyzerLog>
{
    public void Configure(EntityTypeBuilder<AnalyzerLog> b)
    {
        b.ToTable("analyzer_logs", schema: "audit");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.ApiType).HasColumnName("api_type").HasMaxLength(50).IsRequired();
        b.Property(x => x.RequestData).HasColumnName("request_data").IsRequired();
        b.Property(x => x.ResponseData).HasColumnName("response_data").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.HasIndex(x => new { x.ApiType, x.CreatedAt }).HasDatabaseName("ix_analyzer_logs_api_type_created_at");
        b.HasIndex(x => x.ApiType).HasDatabaseName("ix_analyzer_logs_api_type");
        b.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_analyzer_logs_created_at");
    }
}
