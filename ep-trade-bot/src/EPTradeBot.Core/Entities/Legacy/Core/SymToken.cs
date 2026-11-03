// Generated from OpenAlgo table 'symtoken' (database.symbol). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>symtoken</c>.</summary>
public sealed class SymToken
{
    public int Id { get; set; }

    /// <summary>EP Trade bot addition: the broker whose master contract this row belongs to. OpenAlgo held one broker's master at a time; accounts here may be on different brokers.</summary>
    public string Broker { get; set; } = "";

    public string Symbol { get; set; } = "";
    public string Brsymbol { get; set; } = "";
    public string? Name { get; set; }
    public string? Exchange { get; set; }
    public string? Brexchange { get; set; }
    public string? Token { get; set; }
    public string? Expiry { get; set; }
    public double? Strike { get; set; }
    public int? Lotsize { get; set; }
    public string? Instrumenttype { get; set; }
    public double? TickSize { get; set; }
    public double? ContractValue { get; set; }
}
