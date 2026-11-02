using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EPTradeBot.Architecture.Tests;

/// <summary>Rules about the EF Core model itself, checked without a database.</summary>
public sealed class ModelRulesTests
{
    private static readonly IModel Model = BuildModel();

    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<EpTradeDbContext>()
            .UseNpgsql("Host=unused;Database=unused")
            .Options;
        using var db = new EpTradeDbContext(options);
        return db.GetService<IDesignTimeModel>().Model;
    }

    [Fact]
    public void Every_table_lives_in_one_of_the_six_schemas()
    {
        var outside = Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null && !Schemas.All.Contains(e.GetSchema()!))
            .Select(e => $"{e.GetSchema()}.{e.GetTableName()}")
            .ToList();
        Assert.Empty(outside);
    }

    [Fact]
    public void Every_schema_has_tables()
    {
        var used = Model.GetEntityTypes().Select(e => e.GetSchema()).ToHashSet();
        foreach (var schema in Schemas.All) Assert.Contains(schema, used);
    }

    [Fact]
    public void Every_entity_has_its_own_entity_type_configuration_class()
    {
        var configured = typeof(EpTradeDbContext).Assembly.GetTypes()
            .SelectMany(t => t.GetInterfaces())
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEntityTypeConfiguration<>))
            .Select(i => i.GetGenericArguments()[0])
            .ToHashSet();
        var missing = Model.GetEntityTypes().Select(e => e.ClrType).Where(t => !configured.Contains(t)).Select(t => t.FullName).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void All_74_openalgo_tables_are_ported()
    {
        var legacy = Model.GetEntityTypes().Count(e => e.ClrType.Namespace!.StartsWith("EPTradeBot.Core.Entities.Legacy", StringComparison.Ordinal));
        Assert.Equal(74, legacy);
    }

    [Fact]
    public void Candles_have_a_brin_index_on_bar_time()
    {
        var candles = Model.GetEntityTypes().Single(e => e.GetTableName() == "candles");
        Assert.Equal("market_data", candles.GetSchema());
        var brin = candles.GetIndexes().Single(i => i.GetDatabaseName() == "ix_candles_ts_brin");
        Assert.Equal("brin", brin.FindAnnotation("Npgsql:IndexMethod")?.Value);
        Assert.Equal(["Ts"], brin.Properties.Select(p => p.Name));
    }

    [Fact]
    public void Broker_account_type_is_stored_as_LIVE_or_PAPER_text()
    {
        var accounts = Model.GetEntityTypes().Single(e => e.GetTableName() == "broker_accounts");
        var type = accounts.FindProperty("AccountType")!;
        Assert.Equal("core", accounts.GetSchema());
        Assert.Equal(typeof(string), type.GetProviderClrType());
        Assert.False(type.IsNullable);
    }

    [Fact]
    public void No_column_has_a_sql_default_or_computed_expression()
    {
        var offenders = Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties().Select(p => (Entity: e, Property: p)))
            .Where(x => x.Property.GetDefaultValueSql() is not null || x.Property.GetComputedColumnSql() is not null)
            .Select(x => $"{x.Entity.GetTableName()}.{x.Property.GetColumnName()}")
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void No_index_has_a_sql_filter()
    {
        var offenders = Model.GetEntityTypes().SelectMany(e => e.GetIndexes()).Where(i => i.GetFilter() is not null).Select(i => i.GetDatabaseName()).ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_row_version_maps_to_the_xmin_system_column()
    {
        // A row version given an ordinary column name becomes a NOT NULL column that
        // nothing writes, and every insert fails. It must be PostgreSQL's xmin.
        var offenders = Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties().Where(p => p.IsConcurrencyToken).Select(p => (Entity: e, Property: p)))
            .Where(x => x.Property.GetColumnName() != "xmin")
            .Select(x => $"{x.Entity.GetTableName()}.{x.Property.GetColumnName()}")
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Model_has_no_changes_pending_against_the_last_migration()
    {
        var options = new DbContextOptionsBuilder<EpTradeDbContext>().UseNpgsql("Host=unused;Database=unused").Options;
        using var db = new EpTradeDbContext(options);
        Assert.False(db.Database.HasPendingModelChanges(), "The model changed without a migration. Run: dotnet ef migrations add <Name>");
    }
}
