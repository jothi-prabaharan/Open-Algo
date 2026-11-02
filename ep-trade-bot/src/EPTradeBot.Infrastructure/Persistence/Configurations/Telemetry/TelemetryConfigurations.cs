using EPTradeBot.Core.Entities.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Telemetry;

internal sealed class BusLaneStatusConfiguration : IEntityTypeConfiguration<BusLaneStatus>
{
    public void Configure(EntityTypeBuilder<BusLaneStatus> b)
    {
        b.ToTable("bus_lane_status", schema: Schemas.Telemetry);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.LaneName).HasMaxLength(64).IsRequired();
        b.Property(x => x.Status).HasMaxLength(16).IsRequired();
        b.HasIndex(x => new { x.LaneName, x.SampledAtUtc }).HasDatabaseName("ix_bus_lane_status_lane_time");
    }
}

internal sealed class WebSocketProxyConnectionConfiguration : IEntityTypeConfiguration<WebSocketProxyConnection>
{
    public void Configure(EntityTypeBuilder<WebSocketProxyConnection> b)
    {
        b.ToTable("ws_proxy_connections", schema: Schemas.Telemetry);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.ConnectionId).HasMaxLength(64).IsRequired();
        b.Property(x => x.ClientIp).HasMaxLength(45);
        b.Property(x => x.UserAgent).HasMaxLength(300);
        b.Property(x => x.DisconnectReason).HasMaxLength(200);
        b.HasIndex(x => x.ConnectionId).IsUnique().HasDatabaseName("ux_ws_proxy_connections_connection_id");
        b.HasIndex(x => x.ConnectedAtUtc).HasMethod("brin").HasDatabaseName("ix_ws_proxy_connections_connected_brin");
    }
}
