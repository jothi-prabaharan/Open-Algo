// Generated from OpenAlgo table 'login_attempts'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Audit;

internal sealed class LoginAttemptConfiguration : IEntityTypeConfiguration<LoginAttempt>
{
    public void Configure(EntityTypeBuilder<LoginAttempt> b)
    {
        b.ToTable("login_attempts", schema: "audit");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Username).HasColumnName("username").HasMaxLength(255).IsRequired();
        b.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(45);
        b.Property(x => x.DeviceInfo).HasColumnName("device_info").HasMaxLength(500);
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        b.Property(x => x.LoginType).HasColumnName("login_type").HasMaxLength(20);
        b.Property(x => x.Broker).HasColumnName("broker").HasMaxLength(20);
        b.Property(x => x.FailureReason).HasColumnName("failure_reason").HasMaxLength(255);
        b.Property(x => x.Timestamp).HasColumnName("timestamp");
        b.HasIndex(x => x.Timestamp).HasDatabaseName("ix_login_attempts_timestamp");
        b.HasIndex(x => x.Status).HasDatabaseName("ix_login_attempts_status");
        b.HasIndex(x => x.Username).HasDatabaseName("ix_login_attempts_username");
    }
}
