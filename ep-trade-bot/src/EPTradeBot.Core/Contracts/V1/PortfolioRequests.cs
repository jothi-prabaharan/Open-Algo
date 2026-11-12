using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace EPTradeBot.Core.Contracts.V1;

/// <summary>POST /api/v1/sip/backtest</summary>
public sealed class SipBacktestRequest : ApiKeyRequest
{
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [RegularExpression("^(NSE|BSE)$")] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "NSE";
    [Required, StringLength(10, MinimumLength = 10)] [JsonPropertyName("start_date")] public string StartDate { get; set; } = "";
    [Required, StringLength(10, MinimumLength = 10)] [JsonPropertyName("end_date")] public string EndDate { get; set; } = "";
    [Required, Range(1, 10_000_000.0)] [JsonPropertyName("amount")] public double? Amount { get; set; }
    [RegularExpression("^(monthly|fortnightly|weekly|quarterly)$")] [JsonPropertyName("frequency")] public string Frequency { get; set; } = "monthly";
    [Range(1, 28)] [JsonPropertyName("day_of_month")] public int DayOfMonth { get; set; } = 1;
    [Range(0, 100.0)] [JsonPropertyName("step_up_percent")] public double StepUpPercent { get; set; }
    [Range(0, 5.0)] [JsonPropertyName("brokerage_percent")] public double BrokeragePercent { get; set; }
    [Range(0, 10_000.0)] [JsonPropertyName("brokerage_flat")] public double BrokerageFlat { get; set; }
    [RegularExpression("^(indian_equity|flat_bps)$")] [JsonPropertyName("cost_model")] public string CostModel { get; set; } = "indian_equity";
    [RegularExpression("^(NSE|BSE)$")] [JsonPropertyName("cost_exchange")] public string CostExchange { get; set; } = "NSE";
    [JsonPropertyName("charges")] public Dictionary<string, Dictionary<string, double?>>? Charges { get; set; }
    [Range(0, 1.0)] [JsonPropertyName("gst_rate")] public double? GstRate { get; set; }
    [Range(0, 1000.0)] [JsonPropertyName("cost_bps")] public double CostBps { get; set; }
    [Range(0, 0.1)] [JsonPropertyName("slippage")] public double Slippage { get; set; }
    [StringLength(64)] [JsonPropertyName("benchmark")] public string? Benchmark { get; set; }
    [RegularExpression("^(NSE_INDEX|BSE_INDEX|GLOBAL_INDEX)$")] [JsonPropertyName("benchmark_exchange")] public string BenchmarkExchange { get; set; } = "NSE_INDEX";
    [RegularExpression("^(db|api)$")] [JsonPropertyName("source")] public string Source { get; set; } = "db";
    [JsonPropertyName("include_grids")] public bool IncludeGrids { get; set; } = true;
}

public sealed class SectorInstrumentItem
{
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [Required, RegularExpression("^(NSE|BSE|NSE_INDEX|BSE_INDEX|MCX_INDEX|GLOBAL_INDEX)$")] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
    [StringLength(64)] [JsonPropertyName("label")] public string? Label { get; set; }
}

/// <summary>POST /api/v1/sectorrotation</summary>
public sealed class SectorRotationRequest : ApiKeyRequest
{
    [RegularExpression("^(NIFTY|NIFTY500|BANKNIFTY|SENSEX|NIFTYNXT50|MIDCPNIFTY|FINNIFTY)$")] [JsonPropertyName("benchmark")] public string Benchmark { get; set; } = "NIFTY";
    [RegularExpression("^(daily|weekly)$")] [JsonPropertyName("timeframe")] public string Timeframe { get; set; } = "daily";
    [RegularExpression("^(db|api)$")] [JsonPropertyName("source")] public string Source { get; set; } = "db";
    [MaxLength(40)] [JsonPropertyName("instruments")] public List<SectorInstrumentItem> Instruments { get; set; } = [];
}
