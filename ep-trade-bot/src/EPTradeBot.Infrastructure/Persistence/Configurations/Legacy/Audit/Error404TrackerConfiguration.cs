// Generated from OpenAlgo table 'error_404_tracker'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Audit;

internal sealed class Error404TrackerConfiguration : IEntityTypeConfiguration<Error404Tracker>
{
    public void Configure(EntityTypeBuilder<Error404Tracker> b)
    {
        b.ToTable("error_404_tracker", schema: "audit");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(50).IsRequired();
        b.Property(x => x.ErrorCount).HasColumnName("error_count");
        b.Property(x => x.FirstErrorAt).HasColumnName("first_error_at");
        b.Property(x => x.LastErrorAt).HasColumnName("last_error_at");
        b.Property(x => x.PathsAttempted).HasColumnName("paths_attempted");
        b.HasIndex(x => x.IpAddress).HasDatabaseName("ix_error_404_tracker_ip_address");
        b.HasIndex(x => x.ErrorCount).HasDatabaseName("ix_error_404_tracker_error_count");
        b.HasIndex(x => x.FirstErrorAt).HasDatabaseName("ix_error_404_tracker_first_error_at");
    }
}
