using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Mcp;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.Mcp;

/// <summary>Writes each tool call to the audit schema. A failure to write is logged and never fails the call itself.</summary>
public sealed class McpAudit(IDbContextFactory<EpTradeDbContext> contexts, ILogger<McpAudit> logger)
{
    public async Task WriteAsync(McpAuditEntry e)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync();
            db.McpCalls.Add(new McpCallRecord { AtUtc = e.AtUtc, ClientId = e.ClientId, TokenId = e.TokenId, Tool = e.Tool, Scope = e.Scope, ParamsHash = e.ParamsHash, DurationMs = e.DurationMs, Outcome = e.Outcome });
            await db.SaveChangesAsync();
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            logger.LogError(ex, "A tool call over MCP could not be written to the audit log.");
        }
    }
}
