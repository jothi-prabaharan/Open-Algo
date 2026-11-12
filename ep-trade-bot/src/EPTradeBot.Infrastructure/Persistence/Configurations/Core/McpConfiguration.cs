using EPTradeBot.Core.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Core;

internal sealed class McpClientConfiguration : IEntityTypeConfiguration<McpClient>
{
    public void Configure(EntityTypeBuilder<McpClient> b)
    {
        b.ToTable("mcp_clients", schema: Schemas.Core);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.ClientId).HasMaxLength(64).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.RedirectUrisJson).HasColumnType("jsonb").IsRequired();
        b.HasIndex(x => x.ClientId).IsUnique().HasDatabaseName("ux_mcp_clients_client_id");
    }
}

internal sealed class McpAuthorizationCodeConfiguration : IEntityTypeConfiguration<McpAuthorizationCode>
{
    public void Configure(EntityTypeBuilder<McpAuthorizationCode> b)
    {
        b.ToTable("mcp_authorization_codes", schema: Schemas.Core);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.CodeHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.ClientId).HasMaxLength(64).IsRequired();
        b.Property(x => x.Scope).HasMaxLength(100).IsRequired();
        b.Property(x => x.OrderMode).HasMaxLength(10).IsRequired();
        b.Property(x => x.RedirectUri).HasMaxLength(500).IsRequired();
        b.Property(x => x.CodeChallenge).HasMaxLength(128).IsRequired();
        b.HasIndex(x => x.CodeHash).IsUnique().HasDatabaseName("ux_mcp_authorization_codes_code_hash");
        b.HasOne<BrokerAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class McpTokenConfiguration : IEntityTypeConfiguration<McpToken>
{
    public void Configure(EntityTypeBuilder<McpToken> b)
    {
        b.ToTable("mcp_tokens", schema: Schemas.Core);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.ClientId).HasMaxLength(64).IsRequired();
        b.Property(x => x.Scope).HasMaxLength(100).IsRequired();
        b.Property(x => x.OrderMode).HasMaxLength(10).IsRequired();
        b.Property(x => x.AccessHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.RefreshHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.PreviousRefreshHash).HasMaxLength(64);
        b.HasIndex(x => x.AccessHash).IsUnique().HasDatabaseName("ux_mcp_tokens_access_hash");
        b.HasIndex(x => x.RefreshHash).IsUnique().HasDatabaseName("ux_mcp_tokens_refresh_hash");
        b.HasOne<BrokerAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class McpCallRecordConfiguration : IEntityTypeConfiguration<McpCallRecord>
{
    public void Configure(EntityTypeBuilder<McpCallRecord> b)
    {
        b.ToTable("mcp_calls", schema: Schemas.Audit);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.ClientId).HasMaxLength(64).IsRequired();
        b.Property(x => x.TokenId).HasMaxLength(64).IsRequired();
        b.Property(x => x.Tool).HasMaxLength(64).IsRequired();
        b.Property(x => x.Scope).HasMaxLength(20).IsRequired();
        b.Property(x => x.ParamsHash).HasMaxLength(32).IsRequired();
        b.Property(x => x.Outcome).HasMaxLength(20).IsRequired();
        b.HasIndex(x => x.AtUtc).HasDatabaseName("ix_mcp_calls_at");
    }
}
