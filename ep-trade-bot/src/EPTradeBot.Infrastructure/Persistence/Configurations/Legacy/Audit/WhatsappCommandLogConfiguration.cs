// Generated from OpenAlgo table 'whatsapp_command_logs'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Audit;

internal sealed class WhatsappCommandLogConfiguration : IEntityTypeConfiguration<WhatsappCommandLog>
{
    public void Configure(EntityTypeBuilder<WhatsappCommandLog> b)
    {
        b.ToTable("whatsapp_command_logs", schema: "audit");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.WhatsappJid).HasColumnName("whatsapp_jid").HasMaxLength(120).IsRequired();
        b.Property(x => x.Command).HasColumnName("command").HasMaxLength(100).IsRequired();
        b.Property(x => x.Parameters).HasColumnName("parameters");
        b.Property(x => x.ExecutedAt).HasColumnName("executed_at");
        b.HasIndex(x => x.WhatsappJid).HasDatabaseName("ix_whatsapp_command_logs_whatsapp_jid");
        b.HasOne<EPTradeBot.Core.Entities.Legacy.Core.WhatsappUser>().WithMany().HasPrincipalKey(p => p.WhatsappJid).HasForeignKey(x => x.WhatsappJid).OnDelete(DeleteBehavior.Cascade);
    }
}
