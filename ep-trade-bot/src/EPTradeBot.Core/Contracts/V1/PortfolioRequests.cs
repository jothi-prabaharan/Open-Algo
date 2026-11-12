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

public sealed class PortfolioHoldingItem
{
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [RegularExpression("^(NSE|BSE)$")] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "NSE";
    [Required, Range(0, double.MaxValue)] [JsonPropertyName("weight")] public double? Weight { get; set; }
}

/// <summary>POST /api/v1/portfolio/backtest and /api/v1/portfolio/tearsheet</summary>
public sealed class PortfolioBacktestRequest : ApiKeyRequest
{
    [Required, MinLength(1), MaxLength(50)] [JsonPropertyName("holdings")] public List<PortfolioHoldingItem> Holdings { get; set; } = [];
    [Required] [JsonPropertyName("start_date")] public string StartDate { get; set; } = "";
    [Required] [JsonPropertyName("end_date")] public string EndDate { get; set; } = "";
    [JsonPropertyName("benchmark")] public string? Benchmark { get; set; }
    [RegularExpression("^(NSE_INDEX|BSE_INDEX|GLOBAL_INDEX)$")] [JsonPropertyName("benchmark_exchange")] public string BenchmarkExchange { get; set; } = "NSE_INDEX";
    [RegularExpression("^(never|monthly|quarterly|yearly)$")] [JsonPropertyName("rebalance")] public string Rebalance { get; set; } = "never";
    [Range(0, 0.99)] [JsonPropertyName("drift_band")] public double DriftBand { get; set; }
    [RegularExpression("^(indian_equity|flat_bps)$")] [JsonPropertyName("cost_model")] public string CostModel { get; set; } = "indian_equity";
    [Range(0, 0.05)] [JsonPropertyName("brokerage_pct")] public double BrokeragePct { get; set; }
    [RegularExpression("^(NSE|BSE)$")] [JsonPropertyName("cost_exchange")] public string CostExchange { get; set; } = "NSE";
    [JsonPropertyName("charges")] public Dictionary<string, Dictionary<string, double?>> Charges { get; set; } = [];
    [Range(0, 1.0)] [JsonPropertyName("gst_rate")] public double? GstRate { get; set; }
    [Range(0, 1000.0)] [JsonPropertyName("cost_bps")] public double CostBps { get; set; }
    [Range(0, 0.1)] [JsonPropertyName("slippage")] public double Slippage { get; set; }
    [Range(1, double.MaxValue)] [JsonPropertyName("initial_capital")] public double InitialCapital { get; set; } = 100_000.0;
    [Range(0, 0.5)] [JsonPropertyName("risk_free_rate")] public double RiskFreeRate { get; set; }
    [RegularExpression("^(db|api)$")] [JsonPropertyName("source")] public string Source { get; set; } = "db";
}

/// <summary>GET /api/v1/portfolio/benchmarks</summary>
public sealed class PortfolioBenchmarksRequest : ApiKeyRequest;

/// <summary>POST /api/v1/portfolio/holdings</summary>
public sealed class PortfolioHoldingsRequest : ApiKeyRequest
{
    [Range(60, 3650)] [JsonPropertyName("lookback_days")] public int LookbackDays { get; set; } = 365;
    [JsonPropertyName("benchmark")] public string? Benchmark { get; set; } = "NIFTY";
    [RegularExpression("^(NSE_INDEX|BSE_INDEX|GLOBAL_INDEX)$")] [JsonPropertyName("benchmark_exchange")] public string BenchmarkExchange { get; set; } = "NSE_INDEX";
    [Range(0, 0.5)] [JsonPropertyName("risk_free_rate")] public double RiskFreeRate { get; set; }
    [RegularExpression("^(db|api)$")] [JsonPropertyName("source")] public string Source { get; set; } = "db";
}
