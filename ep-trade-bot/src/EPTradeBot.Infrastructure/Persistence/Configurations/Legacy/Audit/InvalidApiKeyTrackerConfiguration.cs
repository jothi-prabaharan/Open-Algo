// Generated from OpenAlgo table 'invalid_api_key_tracker'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Audit;

internal sealed class InvalidApiKeyTrackerConfiguration : IEntityTypeConfiguration<InvalidApiKeyTracker>
{
    public void Configure(EntityTypeBuilder<InvalidApiKeyTracker> b)
    {
        b.ToTable("invalid_api_key_tracker", schema: "audit");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(50).IsRequired();
        b.Property(x => x.AttemptCount).HasColumnName("attempt_count");
        b.Property(x => x.FirstAttemptAt).HasColumnName("first_attempt_at");
        b.Property(x => x.LastAttemptAt).HasColumnName("last_attempt_at");
        b.Property(x => x.ApiKeysTried).HasColumnName("api_keys_tried");
        b.HasIndex(x => x.AttemptCount).HasDatabaseName("ix_invalid_api_key_tracker_attempt_count");
        b.HasIndex(x => x.FirstAttemptAt).HasDatabaseName("ix_invalid_api_key_tracker_first_attempt_at");
        b.HasIndex(x => x.IpAddress).HasDatabaseName("ix_invalid_api_key_tracker_ip_address");
    }
}
