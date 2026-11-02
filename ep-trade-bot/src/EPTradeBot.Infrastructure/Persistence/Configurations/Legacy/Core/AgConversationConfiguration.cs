// Generated from OpenAlgo table 'ag_conversation'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class AgConversationConfiguration : IEntityTypeConfiguration<AgConversation>
{
    public void Configure(EntityTypeBuilder<AgConversation> b)
    {
        b.ToTable("ag_conversation", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.UserId).HasColumnName("user_id").HasMaxLength(80).IsRequired();
        b.Property(x => x.Title).HasColumnName("title").HasMaxLength(300);
        b.Property(x => x.Surface).HasColumnName("surface").HasMaxLength(20).IsRequired();
        b.Property(x => x.AgnoSessionId).HasColumnName("agno_session_id").HasMaxLength(120);
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        b.HasIndex(x => x.AgnoSessionId).HasDatabaseName("ix_ag_conversation_agno_session_id");
        b.HasIndex(x => x.UserId).HasDatabaseName("ix_ag_conversation_user_id");
        b.HasIndex(x => new { x.UserId, x.UpdatedAt }).HasDatabaseName("ix_ag_conversation_user_id_updated_at");
    }
}
