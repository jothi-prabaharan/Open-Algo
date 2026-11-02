// Generated from OpenAlgo table 'active_sessions'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class ActiveSessionConfiguration : IEntityTypeConfiguration<ActiveSession>
{
    public void Configure(EntityTypeBuilder<ActiveSession> b)
    {
        b.ToTable("active_sessions", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Username).HasColumnName("username").HasMaxLength(255).IsRequired();
        b.Property(x => x.SessionId).HasColumnName("session_id").HasMaxLength(64).IsRequired();
        b.Property(x => x.DeviceInfo).HasColumnName("device_info").HasMaxLength(500);
        b.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(45);
        b.Property(x => x.Broker).HasColumnName("broker").HasMaxLength(20);
        b.Property(x => x.LoginTime).HasColumnName("login_time");
        b.Property(x => x.LastSeen).HasColumnName("last_seen");
        b.HasIndex(x => x.SessionId).IsUnique().HasDatabaseName("ux_active_sessions_session_id");
        b.HasIndex(x => x.Username).HasDatabaseName("ix_active_sessions_username");
    }
}
