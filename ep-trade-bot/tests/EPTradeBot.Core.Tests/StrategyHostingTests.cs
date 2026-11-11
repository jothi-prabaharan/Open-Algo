using EPTradeBot.Core.Scheduling;

namespace EPTradeBot.Core.Tests;

public sealed class StrategyHostingTests
{
    [Theory]
    [InlineData("alpha.py", true)]
    [InlineData("momentum/alpha.py", true)]
    [InlineData("a/b/c.py", true)]
    [InlineData("../alpha.py", false)]
    [InlineData("momentum/../../alpha.py", false)]
    [InlineData("/etc/passwd.py", false)]
    [InlineData("C:/x/alpha.py", false)]
    [InlineData("momentum\\alpha.py", false)]
    [InlineData("alpha.sh", false)]
    [InlineData("alpha", false)]
    [InlineData(".hidden.py", false)]
    [InlineData("dir/.hidden/alpha.py", false)]
    [InlineData("dir//alpha.py", false)]
    [InlineData("./alpha.py", false)]
    [InlineData("alpha\0.py", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_plain_relative_python_path_is_a_script(string? path, bool safe) =>
        Assert.Equal(safe, StrategyPathRules.IsSafeRelative(path));

    [Fact]
    public void A_path_that_is_too_long_is_refused() =>
        Assert.False(StrategyPathRules.IsSafeRelative(new string('a', 258) + ".py"));

    private static readonly StrategyEnvironment.Inputs Inputs = new(Guid.Parse("11111111-2222-3333-4444-555555555555"), "alpha", "NFO", "semi_auto", "http://127.0.0.1:5000", "key-123");

    [Fact]
    public void A_strategy_gets_only_the_variables_it_needs_and_none_of_the_platforms_secrets()
    {
        var platform = new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin", ["HOME"] = "/root", ["LANG"] = "C.UTF-8",
            ["Security__AppKey"] = "app-key-secret", ["Security__ApiKeyPepper"] = "pepper-secret", ["Management__AdminToken"] = "admin-secret",
            ["Database__ConnectionString"] = "Host=db;Password=secret", ["ASPNETCORE_URLS"] = "http://0.0.0.0:5000",
        };
        var env = StrategyEnvironment.Build(platform, Inputs);

        Assert.Equal("/usr/bin", env["PATH"]);
        Assert.Equal("C.UTF-8", env["LANG"]);
        Assert.DoesNotContain(env, kv => kv.Key.Contains("Security") || kv.Key.Contains("Management") || kv.Key.Contains("Database") || kv.Key.StartsWith("ASPNETCORE"));
        Assert.DoesNotContain(env.Values, v => v.Contains("secret"));
    }

    [Fact]
    public void The_strategy_is_told_who_it_is_and_how_to_call_back()
    {
        var env = StrategyEnvironment.Build(new Dictionary<string, string>(), Inputs);
        Assert.Equal("11111111222233334444555555555555", env["STRATEGY_ID"]);
        Assert.Equal("alpha", env["STRATEGY_NAME"]);
        Assert.Equal("NFO", env["EPTRADE_STRATEGY_EXCHANGE"]);
        Assert.Equal("semi_auto", env["EPTRADE_EXECUTION_MODE"]);
        Assert.Equal("http://127.0.0.1:5000", env["EPTRADE_HOST"]);
        Assert.Equal("1", env["PYTHONUNBUFFERED"]);
        // The names OpenAlgo's scripts and SDK read.
        Assert.Equal("key-123", env["OPENALGO_API_KEY"]);
        Assert.Equal("key-123", env["EPTRADE_API_KEY"]);
        Assert.Equal("http://127.0.0.1:5000", env["OPENALGO_HOST"]);
        Assert.Equal("NFO", env["OPENALGO_STRATEGY_EXCHANGE"]);
    }

    [Fact]
    public void No_key_means_no_key_variables() =>
        Assert.DoesNotContain(StrategyEnvironment.Build(new Dictionary<string, string>(), Inputs with { ApiKey = null }).Keys, k => k.EndsWith("API_KEY"));

    [Fact]
    public void Two_names_that_sanitise_alike_do_not_share_a_log()
    {
        var a = StrategyEnvironment.LogFileName("my strategy", Guid.NewGuid());
        var b = StrategyEnvironment.LogFileName("my/strategy", Guid.NewGuid());
        Assert.NotEqual(a, b);
        Assert.StartsWith("my_strategy-", a);
        Assert.DoesNotContain("/", b);
        Assert.EndsWith(".log", a);
        Assert.True(StrategyEnvironment.LogFileName(new string('x', 200), Guid.NewGuid()).Length < 90);
    }
}
