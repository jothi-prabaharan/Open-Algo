namespace EPTradeBot.IntegrationTests;

/// <summary>
/// A test that needs a PostgreSQL server. Set <c>EPTRADE_TEST_PG</c> to a connection
/// string for a role that may create databases; each test creates and drops its own.
/// Without it the test is reported as skipped, never as passed.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public const string Variable = "EPTRADE_TEST_PG";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Set {Variable} to a PostgreSQL connection string to run database tests.";
        }
    }
}

/// <summary>A data-driven test that needs a PostgreSQL server; skipped, never passed, without one.</summary>
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable)))
        {
            Skip = $"Set {PostgresFactAttribute.Variable} to a PostgreSQL connection string to run database tests.";
        }
    }
}
