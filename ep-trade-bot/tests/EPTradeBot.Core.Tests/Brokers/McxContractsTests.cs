using EPTradeBot.Core.Brokers.Zerodha;

namespace EPTradeBot.Core.Tests.Brokers;

/// <summary>The MCX lot rules Zerodha needs because Kite counts contracts and OpenAlgo counts units.</summary>
public sealed class McxContractsTests
{
    [Theory]
    [InlineData("GOLDPETAL05DEC30FUT", "GOLDPETAL")]
    [InlineData("GOLD05DEC30FUT", "GOLD")]
    [InlineData("SILVERMIC05DEC30FUT", "SILVERMIC")]
    [InlineData("SILVERM05DEC30FUT", "SILVERM")]
    [InlineData("SILVER05DEC30FUT", "SILVER")]
    [InlineData("CRUDEOILM18DEC30FUT", "CRUDEOILM")]
    [InlineData("crudeoil18dec30fut", "CRUDEOIL")]
    public void The_longest_matching_root_wins(string symbol, string root) => Assert.Equal(root, McxContracts.Root(symbol));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("UNLISTED18DEC30FUT")]
    public void An_unknown_symbol_has_no_root(string? symbol) => Assert.Null(McxContracts.Root(symbol));

    [Fact]
    public void The_master_row_lot_wins_over_the_table()
    {
        Assert.Equal(15, McxContracts.UnitsPerContract("MCXBULLDEX18DEC30FUT", 15));
        Assert.Equal(100, McxContracts.UnitsPerContract("CRUDEOIL18DEC30FUT", 100));
        Assert.Equal(7, McxContracts.UnitsPerContract("CRUDEOIL18DEC30FUT", 7));
    }

    [Fact]
    public void Without_a_master_row_the_table_answers_and_a_revised_root_is_unknown()
    {
        Assert.Equal(100, McxContracts.UnitsPerContract("CRUDEOIL18DEC30FUT", 0));
        Assert.Equal(1, McxContracts.UnitsPerContract("GOLDPETAL05DEC30FUT", 0));
        Assert.Null(McxContracts.UnitsPerContract("MCXBULLDEX18DEC30FUT", 0));
        // An underlying in neither source converts by 1, as OpenAlgo does.
        Assert.Equal(1, McxContracts.UnitsPerContract("UNLISTED18DEC30FUT", 0));
    }

    [Fact]
    public void Units_become_whole_contracts_and_the_sign_is_kept()
    {
        Assert.Equal(2, McxContracts.ToKite(200, "CRUDEOIL18DEC30FUT", 100));
        Assert.Equal(-2, McxContracts.ToKite(-200, "CRUDEOIL18DEC30FUT", 100));
        Assert.Equal(0, McxContracts.ToKite(0, "CRUDEOIL18DEC30FUT", 100));
        Assert.Equal(3, McxContracts.ToKite(3, "GOLD05DEC30FUT", 1));
        Assert.Equal(-3, McxContracts.ToKite(-3, "GOLD05DEC30FUT", 1));
    }

    [Theory]
    [InlineData(150)]
    [InlineData(-150)]
    [InlineData(99)]
    [InlineData(101)]
    public void A_part_lot_is_refused_not_rounded(int units)
    {
        var ex = Assert.Throws<McxQuantityException>(() => McxContracts.ToKite(units, "CRUDEOIL18DEC30FUT", 100, "Disclosed quantity"));
        Assert.Equal($"Disclosed quantity must be in multiples of lot size 100 for CRUDEOIL18DEC30FUT, got {Math.Abs(units)}", ex.Message);
    }

    [Fact]
    public void A_revised_root_with_no_master_row_cannot_be_sized()
    {
        var ex = Assert.Throws<McxQuantityException>(() => McxContracts.ToKite(30, "MCXBULLDEX18DEC30FUT", 0));
        Assert.Contains("MCXBULLDEX18DEC30FUT", ex.Message);
    }

    [Fact]
    public void Contracts_become_units_and_an_unknowable_size_passes_through()
    {
        Assert.Equal(200, McxContracts.FromKite(2, "CRUDEOIL30DECFUT", 100));
        Assert.Equal(-300, McxContracts.FromKite(-3, "CRUDEOIL30DECFUT", 100));
        Assert.Equal(3, McxContracts.FromKite(3, "GOLD30DECFUT", 1));
        Assert.Equal(30, McxContracts.FromKite(2, "MCXBULLDEX30DECFUT", 15));
        Assert.Equal(1, McxContracts.FromKite(1, "MCXBULLDEX30JANFUT", 0));
    }

    [Fact]
    public void Price_is_multiplied_by_the_quotation_unit_where_it_differs_from_the_contract()
    {
        Assert.Equal(100, McxContracts.PriceMultiplier("GOLD05DEC30FUT", 1));
        Assert.Equal(10, McxContracts.PriceMultiplier("SILVER100DEC30FUT", 100));
        // Quoted per contract unit: the multiplier is the contract's own size.
        Assert.Equal(100, McxContracts.PriceMultiplier("CRUDEOIL18DEC30FUT", 100));
        Assert.Equal(1, McxContracts.PriceMultiplier("GOLDPETAL05DEC30FUT", 1));
        Assert.Equal(1, McxContracts.PriceMultiplier("UNLISTED18DEC30FUT", 50));
    }
}
