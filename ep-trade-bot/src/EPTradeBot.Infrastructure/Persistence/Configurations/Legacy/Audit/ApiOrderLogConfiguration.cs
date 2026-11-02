// Generated from OpenAlgo table 'order_logs'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Audit;

internal sealed class ApiOrderLogConfiguration : IEntityTypeConfiguration<ApiOrderLog>
{
    public void Configure(EntityTypeBuilder<ApiOrderLog> b)
    {
        b.ToTable("order_logs", schema: "audit");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.ApiType).HasColumnName("api_type").IsRequired();
        b.Property(x => x.RequestData).HasColumnName("request_data").IsRequired();
        b.Property(x => x.ResponseData).HasColumnName("response_data").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
    }
}
