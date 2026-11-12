using EPTradeBot.Core.Portfolio;

namespace EPTradeBot.Core.Tests;

/// <summary>The generator against numpy's own draws (default_rng(seed).integers), recorded from numpy.</summary>
public sealed class NumpyRandomTests
{
    private static long[] Draw(uint seed, long high, int count)
    {
        var rng = new NumpyRandom(seed);
        return [.. Enumerable.Range(0, count).Select(_ => rng.Integer(0, high))];
    }

    [Fact]
    public void Draws_match_numpy_for_the_monte_carlo_seed() =>
        Assert.Equal(new long[] { 476, 154, 537, 215, 139, 543, 437, 460, 673, 266, 571, 226, 386, 407, 145, 127, 156, 458, 417, 641, 480, 169, 623, 646, 498, 454, 88, 65, 181, 300, 49, 603, 322, 474, 144, 222, 78, 499, 527, 149 }, Draw(12345, 681, 40));

    [Fact]
    public void Draws_match_numpy_for_a_tiny_range() =>
        Assert.Equal(new long[] { 2, 0, 2, 0, 0, 2, 1, 2, 2, 1, 2, 0, 1, 1, 0, 0, 0, 2, 1, 2, 2, 0, 2, 2, 2, 2, 0, 0, 0, 1, 0, 2, 1, 2, 0, 0, 0, 2, 2, 0 }, Draw(12345, 3, 40));

    [Fact]
    public void Draws_match_numpy_for_another_seed_and_a_wide_range() =>
        Assert.Equal(new long[] { 94490, 62509, 68417, 89721, 57829, 77568, 83365, 22520, 5553, 30016, 28506, 87355, 91262, 526, 49978, 82122, 13144, 79706, 11908, 46793, 81646, 30303, 34160, 27842, 71947, 25486, 99045, 44507, 47814, 50454, 58254, 55349, 50944, 99550, 80766, 79266, 70027, 62217, 34102, 98896 }, Draw(7, 100000, 40));
}
