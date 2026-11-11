using System.Text.Json.Nodes;
using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;

namespace EPTradeBot.Contract.Tests;

/// <summary>History and intervals on Zerodha (tools/contract-fixtures/history.fixtures.json).</summary>
public sealed class HistoryHost : BrokerContractHost
{
    public override string BrokerName => "zerodha";
    public override string FixtureFile => "history.fixtures.json";
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["OpenAlgoApi:HistorySpacingMs"] = "0" };
}

public sealed class HistoryContractTests(HistoryHost host, ITestOutputHelper output) : IClassFixture<HistoryHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_unsupported_interval"] = new(
            "OpenAlgo answers 500 with \"Error fetching historical data: Unsupported timeframe: 2m\". EP Trade bot answers 400 and names what Zerodha offers.",
            Status: 400),
        ["history_refused"] = new(
            "Same status; the reason is worded as EP Trade bot's Zerodha plugin words every refusal.",
            f => ContractRunner.Expect(f, 500, ContractRunner.Error("Zerodha refused the request: Incorrect `api_key` or `access_token`."))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

/// <summary>History and intervals on Angel One (tools/contract-fixtures/history_angel.fixtures.json).</summary>
public sealed class HistoryAngelHost : BrokerContractHost
{
    public override string BrokerName => "angel";
    public override string FixtureFile => "history_angel.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "angel-private-key" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>
    {
        ["OpenAlgoApi:HistorySpacingMs"] = "0", ["Brokers:HistoryGapMs:angel"] = "0",
    };
}

public sealed class HistoryAngelContractTests(HistoryAngelHost host, ITestOutputHelper output) : IClassFixture<HistoryAngelHost>
{
    private const string Gap = "OpenAlgo logs a chunk Angel One would not answer, carries on and answers success with the candles it has (none at all, here). EP Trade bot stops and says so, because a history with a missing stretch looks like a quiet market.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_unsupported_interval"] = new(
            "OpenAlgo answers 500 with \"Error fetching historical data: Timeframe '2m' is not supported by Angel\". EP Trade bot answers 400 and names what Angel One offers.",
            Status: 400),
        ["history_session_expired"] = new(Gap, f => ContractRunner.Expect(f, 403, ContractRunner.Error("Angel One says the session has expired. Log in to Angel One again."))),
        ["history_chunk_refused"] = new(Gap, f => ContractRunner.Expect(f, 500,
            ContractRunner.Error("Angel One could not send the candles for 2026-11-02 to 2026-11-02: Something went wrong, please try again after sometime."))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_angel.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

/// <summary>History and intervals on Upstox (tools/contract-fixtures/history_upstox.fixtures.json).</summary>
public sealed class HistoryUpstoxHost : BrokerContractHost
{
    public override string BrokerName => "upstox";
    public override string FixtureFile => "history_upstox.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["OpenAlgoApi:HistorySpacingMs"] = "0" };
}

public sealed class HistoryUpstoxContractTests(HistoryUpstoxHost host, ITestOutputHelper output) : IClassFixture<HistoryUpstoxHost>
{
    private const string Gap = "OpenAlgo logs a chunk Upstox would not answer, carries on and answers success with the candles it has (none at all, here). EP Trade bot stops and says so, because a history with a missing stretch looks like a quiet market.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_interval_upstox_lacks"] = new(
            "OpenAlgo answers 500 with \"Invalid interval: 20m\". EP Trade bot answers 400 and names what Upstox offers.",
            Status: 400),
        ["history_session_expired"] = new(Gap, f => ContractRunner.Expect(f, 403, ContractRunner.Error("Upstox says the access token is invalid or has expired. Log in to Upstox again."))),
        ["history_chunk_refused"] = new(Gap, f => ContractRunner.Expect(f, 500,
            ContractRunner.Error("Upstox could not send the candles for 2026-11-02 to 2026-11-02: UDAPI1021: Invalid Instrument key"))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_upstox.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

/// <summary>History and intervals on Fyers (tools/contract-fixtures/history_fyers.fixtures.json), run with the clock stopped at the recording.</summary>
public sealed class HistoryFyersHost : BrokerContractHost
{
    public override string BrokerName => "fyers";
    public override string FixtureFile => "history_fyers.fixtures.json";
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "FYAPPID-100" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>
    {
        ["OpenAlgoApi:HistorySpacingMs"] = "0", ["Brokers:RetryBackoffMs:fyers"] = "0",
    };
}

public sealed class HistoryFyersContractTests(HistoryFyersHost host, ITestOutputHelper output) : IClassFixture<HistoryFyersHost>
{
    private const string Gap = "OpenAlgo tries a chunk Fyers will not answer four times, carries on and answers success with the candles it has (none at all, here). EP Trade bot stops and says so, because a history with a missing stretch looks like a quiet market.";

    private static Action<JsonObject> Refusal(int status, string message) => f => ContractRunner.Expect(f, status, ContractRunner.Error(message));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_start_after_end"] = new("OpenAlgo answers 500 with a library message. EP Trade bot answers 400 and names the two dates.", Status: 400),
        ["history_weekly_not_offered"] = new("OpenAlgo answers 500 with a multi-line list of what Fyers offers. EP Trade bot answers 400 on one line.", Status: 400),
        ["history_interval_fyers_lacks"] = new("OpenAlgo answers 500 with a multi-line list of what Fyers offers. EP Trade bot answers 400 on one line.", Status: 400),
        ["history_session_expired"] = new(Gap, Refusal(403, "Fyers says the session has expired. Log in to Fyers again.")),
        ["history_chunk_refused_every_time"] = new(Gap, f =>
        {
            var day = f["request"]!["start_date"]!.GetValue<string>();
            ContractRunner.Expect(f, 500, ContractRunner.Error($"Fyers could not send the candles for {day} to {day}: Please provide a valid symbol"));
        }),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_fyers.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

/// <summary>History and intervals on Delta Exchange (tools/contract-fixtures/history_delta.fixtures.json), run with the clock stopped at the recording.</summary>
public sealed class HistoryDeltaHost : BrokerContractHost
{
    public override string BrokerName => "deltaexchange";
    public override string FixtureFile => "history_delta.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiSecret"] = "delta-api-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>
    {
        ["OpenAlgoApi:HistorySpacingMs"] = "0", ["Brokers:RetryBackoffMs:deltaexchange"] = "0",
    };
}

public sealed class HistoryDeltaContractTests(HistoryDeltaHost host, ITestOutputHelper output) : IClassFixture<HistoryDeltaHost>
{
    private static Action<JsonObject> Refusal(int status, string message) => f => ContractRunner.Expect(f, status, ContractRunner.Error(message));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_interval_delta_lacks"] = new("OpenAlgo answers 500 with \"Unsupported interval '2m'\" and its whole map. EP Trade bot answers 400 and names the intervals Delta Exchange offers.", Status: 400),
        ["history_rate_limited_every_time"] = new("OpenAlgo answers 500 with \"History HTTP 429\" and the raw reply. EP Trade bot answers 429 and says to wait.",
            Refusal(429, "Delta Exchange is limiting requests. Wait a moment, then try again.")),
        ["history_server_error"] = new("OpenAlgo answers 500 with \"History HTTP 500\" and the raw reply. EP Trade bot answers 500 with a sentence for the trader.",
            Refusal(500, "Delta Exchange could not send the candles for BTCUSD right now. Try again in a moment.")),
        ["history_symbol_refused"] = new("OpenAlgo answers 500 with the error as a Python dict. EP Trade bot names the reason.",
            Refusal(500, "Delta Exchange refused the candles for BTCUSD: invalid_symbol")),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_delta.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

/// <summary>History and intervals on Dhan (tools/contract-fixtures/history_dhan.fixtures.json), run with the clock stopped at the recording.</summary>
public sealed class HistoryDhanHost : DhanFamilyHost
{
    public override string BrokerName => "dhan";
    public override string FixtureFile => "history_dhan.fixtures.json";
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>
    {
        ["OpenAlgoApi:HistorySpacingMs"] = "0", ["Brokers:RetryBackoffMs:dhan"] = "0",
    };
}

public sealed class HistoryDhanContractTests(HistoryDhanHost host, ITestOutputHelper output) : IClassFixture<HistoryDhanHost>
{
    private const string Silent = "OpenAlgo logs a failed one-day request and answers success with no candles. EP Trade bot says why there are none.";

    private static Action<JsonObject> Refusal(int status, string message) => f => ContractRunner.Expect(f, status, ContractRunner.Error(message));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_unsupported_interval"] = new("OpenAlgo answers 500 with \"Unsupported interval '2m'\" and a list. EP Trade bot answers 400 and names what Dhan offers.", Status: 400),
        ["history_hole_between_chunks"] = new("Same status; the reason is worded for the trader and names the days Dhan sent nothing for.", Status: 500),
        ["history_chunk_refused_three_times"] = new("Same status; the reason is worded for the trader.", Status: 500),
        ["history_one_day_refused"] = new(Silent, Refusal(500, "Dhan needs its market data service. Subscribe to it in your Dhan account.")),
        ["history_token_expired"] = new("OpenAlgo reads Dhan's refusal of the token as a day with no candles. EP Trade bot says the token has expired.",
            Refusal(403, "Dhan says the access token is invalid or has expired. Log in to Dhan again.")),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_dhan.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

/// <summary>History and intervals on Dhan Sandbox (tools/contract-fixtures/history_dhan_sandbox.fixtures.json), run with the clock stopped at the recording.</summary>
public sealed class HistoryDhanSandboxHost : DhanFamilyHost
{
    public override string BrokerName => "dhan_sandbox";
    public override string FixtureFile => "history_dhan_sandbox.fixtures.json";
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>
    {
        ["OpenAlgoApi:HistorySpacingMs"] = "0", ["Brokers:RetryBackoffMs:dhan_sandbox"] = "0",
    };
}

public sealed class HistoryDhanSandboxContractTests(HistoryDhanSandboxHost host, ITestOutputHelper output) : IClassFixture<HistoryDhanSandboxHost>
{
    private const string Invented = "OpenAlgo passes every intraday candle through a mock-realism step that invents the prices and the open interest. EP Trade bot returns the sandbox's candles as they are, because a price nobody traded at looks like a real one to a chart or a backtest.";
    private const string Silent = "OpenAlgo logs a failed request and answers success with no candles. EP Trade bot says why there are none.";

    private static Action<JsonObject> Refusal(int status, string message) => f => ContractRunner.Expect(f, status, ContractRunner.Error(message));

    /// <summary>The candles the sandbox sent, as they are: sorted, one to a timestamp (the first kept), open interest from the reply.</summary>
    private static void RawCandles(JsonObject f)
    {
        var rows = new SortedDictionary<long, JsonObject>();
        var replies = f["broker"]!["POST /v2/charts/intraday"]!;
        foreach (var reply in replies is JsonArray list ? list.OfType<JsonObject>() : [(JsonObject)replies])
        {
            var body = reply["body"]!;
            var times = body["timestamp"]!.AsArray();
            for (var i = 0; i < times.Count; i++)
            {
                double At(string field) => body[field]!.AsArray()[i]!.GetValue<double>();
                rows.TryAdd(times[i]!.GetValue<long>(), new JsonObject
                {
                    ["timestamp"] = times[i]!.GetValue<long>(), ["open"] = At("open"), ["high"] = At("high"), ["low"] = At("low"), ["close"] = At("close"),
                    ["volume"] = (long)At("volume"), ["oi"] = (long)At("open_interest"),
                });
            }
        }

        f["expected"]!["body"]!["data"] = new JsonArray(rows.Values.Select(r => (JsonNode?)r).ToArray());
    }

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_unsupported_interval"] = new("OpenAlgo answers 500 with \"Unsupported interval '2m'\" and a list. EP Trade bot answers 400 and names what Dhan Sandbox offers.", Status: 400),
        ["history_5m_one_day"] = new(Invented, RawCandles),
        ["history_5m_futures_with_open_interest"] = new(Invented, RawCandles),
        ["history_week_one_request"] = new(Invented, RawCandles),
        ["history_chunked_90_days"] = new(Invented, RawCandles),
        ["history_one_day_refused"] = new(Silent, Refusal(500, "Dhan Sandbox needs its market data service. Subscribe to it in your Dhan Sandbox account.")),
        ["history_week_refused"] = new(Silent + " It also asks for the next chunk of days after the first one failed; EP Trade bot stops at the first.", f =>
        {
            ContractRunner.Expect(f, 500, ContractRunner.Error("Dhan Sandbox needs its market data service. Subscribe to it in your Dhan Sandbox account."));
            var sent = f["sent"]!.AsArray();
            while (sent.Count > 1) sent.RemoveAt(sent.Count - 1);
        }),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_dhan_sandbox.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

/// <summary>/ticker on Zerodha (tools/contract-fixtures/ticker.fixtures.json).</summary>
public sealed class TickerHost : BrokerContractHost
{
    public override string BrokerName => "zerodha";
    public override string FixtureFile => "ticker.fixtures.json";
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
}

public sealed class TickerContractTests(TickerHost host, ITestOutputHelper output) : IClassFixture<TickerHost>
{
    private const string Refused = "Same status; the reason is worded as EP Trade bot's Zerodha plugin words every refusal.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["ticker_symbol_without_exchange"] = new(
            "OpenAlgo reads a symbol written without an exchange as NSE:RELIANCE, whatever it was, and answers for that instrument (here it fails, because RELIANCE is not in the recording's master contract). EP Trade bot asks for EXCHANGE:SYMBOL.",
            Status: 400),
        ["ticker_unknown_symbol"] = new("OpenAlgo answers 500 with the plugin's text, because the ticker skips the master contract check. EP Trade bot names the symbol, as /history does.", Status: 400),
        ["ticker_interval_zerodha_lacks"] = new("OpenAlgo answers 500 with \"Unsupported timeframe: W\". EP Trade bot answers 400 and names what Zerodha offers.", Status: 400),
        ["ticker_broker_refused"] = new(Refused, f => ContractRunner.Expect(f, 500, ContractRunner.Error("Zerodha refused the request: Incorrect `api_key` or `access_token`."))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("ticker.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}
