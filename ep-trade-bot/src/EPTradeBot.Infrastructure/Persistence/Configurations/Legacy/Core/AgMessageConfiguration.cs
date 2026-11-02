// Generated from OpenAlgo table 'ag_message'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class AgMessageConfiguration : IEntityTypeConfiguration<AgMessage>
{
    public void Configure(EntityTypeBuilder<AgMessage> b)
    {
        b.ToTable("ag_message", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.ConversationId).HasColumnName("conversation_id").IsRequired();
        b.Property(x => x.Role).HasColumnName("role").HasMaxLength(20).IsRequired();
        b.Property(x => x.Content).HasColumnName("content").IsRequired();
        b.Property(x => x.Tools).HasColumnName("tools").HasColumnType("jsonb");
        b.Property(x => x.Notices).HasColumnName("notices").HasColumnType("jsonb");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.HasIndex(x => new { x.ConversationId, x.CreatedAt }).HasDatabaseName("ix_ag_message_conversation_id_created_at");
        b.HasIndex(x => x.ConversationId).HasDatabaseName("ix_ag_message_conversation_id");
        b.HasOne<AgConversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
    }
}
