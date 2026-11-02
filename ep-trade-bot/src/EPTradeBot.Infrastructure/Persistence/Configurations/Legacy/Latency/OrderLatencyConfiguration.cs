// Generated from OpenAlgo table 'order_latency'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Latency;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Latency;

internal sealed class OrderLatencyConfiguration : IEntityTypeConfiguration<OrderLatency>
{
    public void Configure(EntityTypeBuilder<OrderLatency> b)
    {
        b.ToTable("order_latency", schema: "latency");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Timestamp).HasColumnName("timestamp");
        b.Property(x => x.OrderId).HasColumnName("order_id").HasMaxLength(100).IsRequired();
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.Broker).HasColumnName("broker").HasMaxLength(50);
        b.Property(x => x.Symbol).HasColumnName("symbol").HasMaxLength(50);
        b.Property(x => x.OrderType).HasColumnName("order_type").HasMaxLength(20);
        b.Property(x => x.RttMs).HasColumnName("rtt_ms");
        b.Property(x => x.ValidationLatencyMs).HasColumnName("validation_latency_ms");
        b.Property(x => x.ResponseLatencyMs).HasColumnName("response_latency_ms");
        b.Property(x => x.OverheadMs).HasColumnName("overhead_ms");
        b.Property(x => x.TotalLatencyMs).HasColumnName("total_latency_ms").IsRequired();
        b.Property(x => x.RequestBody).HasColumnName("request_body").HasColumnType("jsonb");
        b.Property(x => x.ResponseBody).HasColumnName("response_body").HasColumnType("jsonb");
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
        b.Property(x => x.Error).HasColumnName("error").HasMaxLength(500);
    }
}
