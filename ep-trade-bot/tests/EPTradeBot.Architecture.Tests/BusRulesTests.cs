using System.Text.RegularExpressions;

namespace EPTradeBot.Architecture.Tests;

/// <summary>
/// The market-data bus is fan-in: the proxy's SUB socket is the single binder of the
/// fixed bus port and every publisher connects. A publisher that binds races the
/// proxy for the port, and the loser slides to another port where subscribing still
/// succeeds but no tick ever arrives (OpenAlgo's ZeroMQ invariant).
/// </summary>
public class BusRulesTests
{
    private static IEnumerable<(string File, string Text)> Sources() =>
        Directory.EnumerateFiles(Path.Combine(RawSqlBanTests.SolutionRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (f, File.ReadAllText(f)));

    [Fact]
    public void Only_the_bus_subscriber_binds_a_socket()
    {
        // Configuration binding (AddOptions().Bind) is not a socket; only files that use NetMQ are checked.
        var binders = Sources().Where(s => s.Text.Contains("using NetMQ", StringComparison.Ordinal) && Regex.IsMatch(s.Text, @"\.Bind\("))
            .Select(s => Path.GetFileName(s.File)).ToList();
        Assert.Equal(["MarketDataBus.cs"], binders);
    }

    [Fact]
    public void Publishers_connect_and_never_bind()
    {
        var publishers = Sources().Where(s => s.Text.Contains("new PublisherSocket(", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(publishers);
        Assert.All(publishers, p =>
        {
            Assert.Contains(".Connect(", p.Text);
            Assert.DoesNotMatch(@"\.Bind\(", p.Text[p.Text.IndexOf("class MarketDataPublisher", StringComparison.Ordinal)..]);
        });
    }

    [Fact]
    public void The_bus_port_is_never_scanned_or_moved()
    {
        // No port arithmetic on the bus port and no fallback loop: the port comes from configuration only.
        foreach (var (file, text) in Sources())
        {
            Assert.DoesNotMatch(@"BusPort\s*(\+|\+\+|\+=|-=)", text);
            Assert.False(Regex.IsMatch(text, @"BusPort\s*=") && !file.EndsWith("Options.cs", StringComparison.Ordinal), $"{Path.GetFileName(file)} assigns the bus port.");
        }
    }
}
