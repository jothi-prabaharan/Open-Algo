using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.HdfcSky;

/// <summary>
/// HDFC Sky margin (broker/hdfcsky/api/margin_api.py, mapping/margin_data.py). One calculator, POST /oapi/v1/margin, takes every leg and returns a portfolio-netted figure and one per leg. A derivative leg also
/// carries the last price of its underlying (a whole number, found on the index exchange first), read for all legs with one /fetch-ltp call. The combined figure is used; when it is empty the legs are summed.
/// </summary>
public sealed partial class HdfcSkyPlugin
{
    private static readonly string[] MarginComponents = ["span", "exposure_margin", "premium_margin", "var_margin", "extreme_loss_margin", "delivery_margin", "additional_margin", "span_spread_margin", "somtier_margin"];

    private static string Segment(string exchange) => exchange switch { "NFO" or "BFO" => "FutOpt", "CDS" => "Currency", "MCX" => "Commodities", _ => "Capital" };

    private static string ProductCode(string? product) => (product ?? "MIS").ToUpperInvariant() switch { "NRML" => "0", "CNC" => "1", "MIS" => "2", _ => "2" };

    private static string PyFloat(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }

    private static (double Total, double Span, double Exposure) SumComponents(JsonNode? block)
    {
        if (block is not JsonObject o) return (0, 0, 0);
        var total = MarginComponents.Sum(k => Flt(o[k])) - Flt(o["premium_benefit"]);
        return (Math.Max(total, 0.0), Flt(o["span"]), Flt(o["exposure_margin"]));
    }

    public async Task<JsonObject> CalculateMarginAsync(BrokerSession session, IReadOnlyList<MarginLeg> legs, CancellationToken cancellationToken)
    {
        // The underlying of each derivative leg, on the index exchange of its group first.
        var wanted = new Dictionary<(string Exchange, string BrokerSymbol), (string Exchange, string Token)>();
        foreach (var leg in legs)
        {
            var r = leg.Instrument;
            if (r.InstrumentType == "EQ" || string.IsNullOrEmpty(r.Name)) continue;
            var candidates = r.Exchange switch { "NFO" => new[] { "NSE_INDEX", "NSE" }, "BFO" => ["BSE_INDEX", "BSE"], _ => [] };
            foreach (var candidate in candidates)
            {
                if (await symbols.FindAsync(Name, r.Name, candidate, cancellationToken) is not { } match) continue;
                wanted[(r.Exchange, r.BrokerSymbol ?? "")] = (LtpExchange(match.Exchange), match.Token);
                break;
            }
        }

        var prices = wanted.Count == 0 ? [] : await FetchLtpAsync(session, wanted.Values.Distinct().ToList(), cancellationToken);

        var data = new JsonArray();
        foreach (var leg in legs)
        {
            var r = leg.Instrument;
            var underlying = wanted.TryGetValue((r.Exchange, r.BrokerSymbol ?? ""), out var key) && prices.TryGetValue(key, out var quote) ? quote.Ltp : 0.0;
            data.Add(new JsonObject
            {
                ["segment"] = Segment(r.Exchange), ["series"] = SeriesType(r), ["exchange"] = r.BrokerExchange, ["side"] = leg.Action.ToUpperInvariant(), ["mode"] = "NEW", ["symbol"] = r.BrokerSymbol,
                ["underlying"] = (long)Math.Round(underlying, MidpointRounding.ToEven), ["token"] = r.Token, ["quantity"] = leg.Quantity.ToString(CultureInfo.InvariantCulture),
                ["price"] = PyFloat((double)leg.Price), ["product"] = ProductCode(leg.Product),
            });
        }

        var (status, body) = await CallAsync(session, HttpMethod.Post, "/oapi/v1/margin", null, new JsonObject { ["data"] = data }, false, cancellationToken);
        if (body is not JsonObject payload) throw new BrokerException($"{DisplayName} sent a margin answer EP Trade bot could not read. Try again in a moment.", 502);

        // Two error shapes exist: the usual {"status": "error"} envelope and the calculator's own {"error": {"code": n, "message": "..."}}.
        var errorBlock = payload["error"] as JsonObject ?? [];
        if (Str(payload["status"]) == "error" || Truthy(errorBlock["code"]))
        {
            var message = Str(errorBlock["message"]) is { Length: > 0 } m ? m : Str(payload["message"]) is { Length: > 0 } pm ? pm : "Failed to calculate margin";
            throw new BrokerException(message, 400);
        }

        var result = payload["result"] as JsonObject ?? [];
        var combined = result["combined_margin"] as JsonObject ?? [];
        var individuals = (result["individual_margin_values"] as JsonArray ?? []).ToList();
        var (total, span, exposure) = SumComponents(combined);
        if (total <= 0 && individuals.Count > 0)
        {
            span = exposure = total = 0.0;
            foreach (var one in individuals)
            {
                var (t, sp, ex) = SumComponents(one);
                total += t;
                span += sp;
                exposure += ex;
            }
        }

        return new JsonObject { ["total_margin_required"] = BookFormatting.Round2(total), ["span_margin"] = BookFormatting.Round2(span), ["exposure_margin"] = BookFormatting.Round2(exposure) };
    }
}
