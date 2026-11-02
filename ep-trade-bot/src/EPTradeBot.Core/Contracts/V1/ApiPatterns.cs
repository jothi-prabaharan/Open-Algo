namespace EPTradeBot.Core.Contracts.V1;

/// <summary>
/// Allowed-value patterns shared by the /api/v1 request DTOs. Each one is the
/// <c>OneOf</c> list of the OpenAlgo marshmallow schema it replaces, so a value
/// OpenAlgo refused is refused here too.
/// </summary>
public static class ApiPatterns
{
    public const string Exchange = "^(NSE|NFO|CDS|BSE|BFO|BCD|MCX|NCDEX|NCO|NSE_INDEX|BSE_INDEX|MCX_INDEX|GLOBAL_INDEX|CRYPTO)$";
    public const string FnoExchange = "^(NFO|BFO|MCX|CDS|NCO|BCD|NCDEX|CRYPTO)$";
    public const string Action = "^(BUY|SELL|buy|sell)$";
    public const string PriceType = "^(MARKET|LIMIT|SL|SL-M)$";
    public const string Product = "^(MIS|NRML|CNC)$";
    public const string OptionsProduct = "^(MIS|NRML)$";
    public const string GttProduct = "^(NRML|CNC)$";
    public const string GttPriceType = "^(LIMIT|MARKET)$";
    public const string GttTriggerType = "^(SINGLE|OCO|single|oco)$";
    public const string OptionType = "^(CE|PE|ce|pe)$";
    public const string OptionOffset = "^(?i:ATM|ITM([1-9]|[1-4][0-9]|50)|OTM([1-9]|[1-4][0-9]|50))$";
    public const string HistoryInterval = "^(1s|5s|10s|15s|30s|45s|1m|2m|3m|5m|10m|15m|20m|30m|1h|2h|3h|4h|D|W|M|Q|Y)$";
    public const string TickerInterval = "^(1m|5m|15m|30m|1h|4h|D|W|M)$";
    public const string DateOrEpoch = @"^(\d{4}-\d{2}-\d{2}|\d{10,13})$";
    public const string IsoDate = @"^\d{4}-\d{2}-\d{2}$";
    public const string ExpiryDdMmmYy = "^[0-9]{2}[A-Za-z]{3}[0-9]{2}$";
    public const string HourMinute = "^([01][0-9]|2[0-3]):[0-5][0-9]$";
    public const string RunMode = "^(live|sandbox)$";
    public const string StrategyStatus = "^(stopped|running|paused|errored)$";
    public const string EventSeverity = "^(info|warn|critical)$";
    public const string EventKind =
        "^(strategy_created|strategy_updated|webhook_token_rotated|live_enabled|live_disabled|webhook_locked|webhook_unlocked|" +
        "run_started|run_paused|run_resumed|run_stop_requested|run_stopped|run_stop_failed|flip_outgoing_exit_rejected|close_all_manual|" +
        "leg_entry_placed|leg_entry_filled|leg_entry_rejected|leg_exit_placed|leg_exit_filled|leg_exit_rejected|leg_close_manual|" +
        "leg_expiry_fallback|order_ack_unrecorded|leg_sl_hit|leg_target_hit|leg_trail_armed|leg_trail_advanced|overall_sl_hit|" +
        "overall_target_hit|lock_profit_armed|lock_profit_floor_advanced|lock_profit_triggered|trail_to_entry_activated|eod_squareoff|" +
        "expiry_squareoff|tick_source_switched_to_polling|tick_source_switched_to_ws|tick_source_stale|recovery_succeeded|recovery_failed)$";

    /// <summary>Exchanges that accept fractional quantities.</summary>
    public static readonly IReadOnlySet<string> CryptoExchanges = new HashSet<string>(StringComparer.Ordinal) { "CRYPTO" };
}
