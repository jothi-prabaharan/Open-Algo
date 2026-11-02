// Generated from OpenAlgo table 'users'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Username).HasColumnName("username").HasMaxLength(80).IsRequired();
        b.Property(x => x.Email).HasColumnName("email").HasMaxLength(120).IsRequired();
        b.Property(x => x.PasswordHash).HasColumnName("password_hash").HasMaxLength(255).IsRequired();
        b.Property(x => x.TotpSecret).HasColumnName("totp_secret").HasMaxLength(255).IsRequired();
        b.Property(x => x.IsAdmin).HasColumnName("is_admin");
        b.Property(x => x.TotpEnabled).HasColumnName("totp_enabled").IsRequired();
        b.Property(x => x.TotpRequiredForLogin).HasColumnName("totp_required_for_login").IsRequired();
        b.Property(x => x.TotpRequiredForMcp).HasColumnName("totp_required_for_mcp").IsRequired();
        b.Property(x => x.TotpRequiredForPasswordReset).HasColumnName("totp_required_for_password_reset").IsRequired();
        b.HasIndex(x => x.Username).IsUnique().HasDatabaseName("ux_users_username");
        b.HasIndex(x => x.Email).IsUnique().HasDatabaseName("ux_users_email");
    }
}
