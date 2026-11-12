using System.Globalization;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Options;

/// <summary>The Greeks of one option from its price: OpenAlgo's calculate_greeks, with the clock passed in. Answers are the JSON the tool returns.</summary>
public static class OptionGreeks
{
    /// <summary>Years and days to an expiry given as an Indian wall-clock time; (0, 0) once it has passed, and never under 0.0001 years.</summary>
    public static (double Years, double Days) TimeToExpiry(DateTime expiry, DateTime nowIst)
    {
        if (expiry < nowIst) return (0.0, 0.0);
        var days = (expiry - nowIst).TotalSeconds / (60 * 60 * 24);
        var years = days / 365.0;
        if (years < 0.0001) { years = 0.0001; days = years * 365.0; }
        return (years, days);
    }

    private static JsonObject Error(string message) => new() { ["status"] = "error", ["message"] = message };

    public static (bool Ok, JsonObject Response, int Code) Calculate(string optionSymbol, string exchange, double spot, double optionPrice, double? interestRate, string? expiryTime, DateTime nowIst)
    {
        string baseSymbol, optType;
        DateTime expiry;
        double strike;
        try
        {
            (baseSymbol, expiry, strike, optType) = OptionSymbols.Parse(optionSymbol, exchange, expiryTime);
        }
        catch (OptionSymbols.RefusedException e)
        {
            return (false, Error(e.Message), 400);
        }

        var (years, days) = TimeToExpiry(expiry, nowIst);
        if (years <= 0) return (false, Error($"Option has expired on {expiry.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture)}"), 400);

        var rate = interestRate ?? 0.0;
        var r = rate / 100.0;
        if (spot <= 0 || optionPrice <= 0) return (false, Error("Spot price and option price must be positive"), 400);
        if (strike <= 0) return (false, Error("Strike price must be positive"), 400);

        var call = optType == "CE";
        var intrinsic = Math.Max(call ? spot - strike : strike - spot, 0.0);
        var timeValue = optionPrice - intrinsic;
        JsonObject Base() => new()
        {
            ["status"] = "success", ["symbol"] = optionSymbol, ["exchange"] = exchange, ["underlying"] = baseSymbol, ["strike"] = PyNumber.Round(strike, 2), ["option_type"] = optType,
            ["expiry_date"] = expiry.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture), ["days_to_expiry"] = PyNumber.Round(days, 4), ["spot_price"] = PyNumber.Round(spot, 2),
            ["option_price"] = PyNumber.Round(optionPrice, 2),
        };

        JsonObject Theoretical(string note)
        {
            var o = Base();
            o["intrinsic_value"] = PyNumber.Round(intrinsic, 2);
            o["time_value"] = PyNumber.Round(Math.Max(timeValue, 0), 2);
            o["interest_rate"] = PyNumber.Round(rate, 2);
            o["implied_volatility"] = 0;
            o["greeks"] = new JsonObject { ["delta"] = call ? 1.0 : -1.0, ["gamma"] = 0, ["theta"] = 0, ["vega"] = 0, ["rho"] = 0 };
            o["note"] = note;
            return o;
        }

        if (timeValue <= 0 || (intrinsic > 0 && timeValue < 0.01)) return (true, Theoretical("Deep ITM option with no time value - theoretical Greeks returned"), 200);

        var iv = Black76.ImpliedVolatility(optionPrice, spot, strike, r, years, call, out var why);
        if (iv is null)
        {
            if (why is not null && (why.Contains("intrinsic", StringComparison.OrdinalIgnoreCase) || why.Contains("below", StringComparison.OrdinalIgnoreCase) || why.Contains("convergence", StringComparison.OrdinalIgnoreCase)))
                return (true, Theoretical("IV calculation not possible - theoretical deep ITM Greeks returned"), 200);
            return (false, Error($"Failed to calculate Implied Volatility: {why}"), 500);
        }

        var sigma = iv.Value;
        var response = Base();
        response["interest_rate"] = PyNumber.Round(rate, 2);
        response["implied_volatility"] = PyNumber.Round(sigma * 100.0, 2);
        response["greeks"] = new JsonObject
        {
            ["delta"] = PyNumber.Round(Black76.Delta(call, spot, strike, years, r, sigma), 4),
            ["gamma"] = PyNumber.Round(Black76.Gamma(spot, strike, years, r, sigma), 6),
            ["theta"] = PyNumber.Round(Black76.Theta(call, spot, strike, years, r, sigma), 4),
            ["vega"] = PyNumber.Round(Black76.Vega(spot, strike, years, r, sigma), 4),
            ["rho"] = PyNumber.Round(Black76.Rho(call, spot, strike, years, r, sigma), 6),
        };
        return (true, response, 200);
    }

    /// <summary>A stable answer for a leg that has expired in a batch: its intrinsic value and zero Greeks.</summary>
    public static JsonObject ExpiredResponse(string optionSymbol, string exchange, string baseSymbol, DateTime expiry, double strike, string optionType, double spot, double optionPrice, double? interestRate)
    {
        var intrinsic = Math.Max(optionType == "CE" ? spot - strike : strike - spot, 0);
        return new JsonObject
        {
            ["status"] = "success", ["symbol"] = optionSymbol, ["exchange"] = exchange, ["underlying"] = baseSymbol, ["strike"] = PyNumber.Round(strike, 2), ["option_type"] = optionType,
            ["expiry_date"] = expiry.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture), ["days_to_expiry"] = 0, ["spot_price"] = PyNumber.Round(spot, 2), ["option_price"] = PyNumber.Round(optionPrice, 2),
            ["intrinsic_value"] = PyNumber.Round(intrinsic, 2), ["time_value"] = PyNumber.Round(Math.Max(optionPrice - intrinsic, 0), 2), ["interest_rate"] = PyNumber.Round(interestRate ?? 0, 2),
            ["implied_volatility"] = 0, ["greeks"] = new JsonObject { ["delta"] = 0, ["gamma"] = 0, ["theta"] = 0, ["vega"] = 0, ["rho"] = 0 }, ["note"] = "Option has expired - Greeks are no longer applicable",
        };
    }

    public static bool IsExpiredResponse(JsonObject response) => (OptionChainReading.Text(response, "message") ?? "").Contains("option has expired", StringComparison.OrdinalIgnoreCase);
}
