using System.Text.Json.Nodes;
using EPTradeBot.Core.Options;

namespace EPTradeBot.Core.Tests;

/// <summary>The option maths against opengreeks, which OpenAlgo's Greeks, IV smile, vol surface and GEX tools all call (black76.fixtures.json, from record_black76.py).</summary>
public sealed class Black76ParityTests
{
    private static readonly JsonObject Fixtures = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "black76.fixtures.json")))!.AsObject();

    private static double N(JsonNode? n) => n!.GetValue<double>();

    private static void Close(double expected, double actual, double relative, string what, JsonNode? row)
    {
        var tolerance = relative * Math.Max(1.0, Math.Abs(expected));
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"{what}: opengreeks {expected:R}, ours {actual:R} for {row!.ToJsonString()}");
    }

    private static bool AtIntrinsic(JsonNode row, bool call)
    {
        double f = N(row["f"]), k = N(row["k"]), t = N(row["t"]), r = N(row["r"]);
        var intrinsic = Math.Exp(-r * t) * Math.Max(call ? f - k : k - f, 0.0);
        return intrinsic > 0 && Math.Abs(N(row["price"]) - intrinsic) <= 1e-13 * intrinsic;
    }

    [Fact]
    public void Price_and_the_five_Greeks_match_opengreeks_over_the_whole_grid()
    {
        var rows = Fixtures["grid"]!.AsArray();
        Assert.True(rows.Count > 2000);
        foreach (var row in rows)
        {
            var call = row!["flag"]!.GetValue<string>() == "c";
            double f = N(row["f"]), k = N(row["k"]), t = N(row["t"]), r = N(row["r"]), s = N(row["sigma"]);
            Close(N(row["price"]), Black76.Price(call, f, k, t, r, s), 1e-9, "price", row);
            Close(N(row["delta"]), Black76.Delta(call, f, k, t, r, s), 1e-9, "delta", row);
            Close(N(row["gamma"]), Black76.Gamma(f, k, t, r, s), 1e-9, "gamma", row);
            Close(N(row["theta"]), Black76.Theta(call, f, k, t, r, s), 1e-9, "theta", row);
            Close(N(row["vega"]), Black76.Vega(f, k, t, r, s), 1e-9, "vega", row);
            Close(N(row["rho"]), Black76.Rho(call, f, k, t, r, s), 1e-9, "rho", row);
        }
    }

    [Fact]
    public void Implied_volatility_matches_opengreeks_and_refuses_the_same_prices()
    {
        var solved = 0;
        var flat = 0;
        var boundary = 0;
        foreach (var row in Fixtures["grid"]!.AsArray())
        {
            var call = row!["flag"]!.GetValue<string>() == "c";
            var iv = Black76.ImpliedVolatility(N(row["price"]), N(row["f"]), N(row["k"]), N(row["r"]), N(row["t"]), call, out var error);
            if (AtIntrinsic(row, call))
            {
                // A price equal to intrinsic value to the last digit is refused or solved by rounding alone, in opengreeks as here. OpenAlgo's Greeks service never asks: a time value of
                // nothing returns the theoretical Greeks before the solver is called, which Greeks tests cover.
                boundary++;
                continue;
            }

            if (row["iv"] is null)
            {
                Assert.Null(iv);
                Assert.Equal(row["iv_error"]!.GetValue<string>(), error);
            }
            else
            {
                Assert.True(iv.HasValue, $"no volatility for {row.ToJsonString()}: {error}");
                var call2 = call;
                double f = N(row["f"]), k = N(row["k"]), t = N(row["t"]), r = N(row["r"]), price = N(row["price"]);
                // Where the price barely moves with volatility (vega under 1e-4), many volatilities give the same price to the last digit: opengreeks returns one of them and any is as right.
                // There the check is that both reprice the option, which is what an implied volatility is for.
                if (N(row["vega"]) >= 1e-4) Close(N(row["iv"]), iv.Value, 1e-6, "iv", row);
                else
                {
                    Close(price, Black76.Price(call2, f, k, t, r, N(row["iv"])), 1e-9, "opengreeks reprice", row);
                    Close(price, Black76.Price(call2, f, k, t, r, iv.Value), 1e-9, "our reprice", row);
                    flat++;
                }

                solved++;
            }
        }

        Assert.True(solved > 2000, $"{solved} solved");
        Assert.True(boundary < 400, $"{boundary} cases skipped at the intrinsic boundary");
        Assert.True(flat > 0 && flat < solved / 4, $"{flat} flat cases");
    }

    [Fact]
    public void The_edge_prices_give_the_same_answer_or_the_same_reason()
    {
        foreach (var row in Fixtures["edge"]!.AsArray())
        {
            var call = row!["flag"]!.GetValue<string>() == "c";
            var iv = Black76.ImpliedVolatility(N(row["price"]), N(row["f"]), N(row["k"]), N(row["r"]), N(row["t"]), call, out var error);
            if (row["iv"] is null)
            {
                Assert.Null(iv);
                Assert.Equal(row["iv_error"]!.GetValue<string>(), error);
            }
            else
            {
                Assert.True(iv.HasValue, row.ToJsonString());
                Close(N(row["iv"]), iv.Value, 1e-6, "iv", row);
            }
        }
    }
}
