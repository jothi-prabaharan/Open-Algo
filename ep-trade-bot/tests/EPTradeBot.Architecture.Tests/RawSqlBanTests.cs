using System.Text.RegularExpressions;

namespace EPTradeBot.Architecture.Tests;

/// <summary>
/// Zero raw SQL, enforced over the whole solution (application code, tests and
/// EF Core migrations). The banned-API analyzer already fails the build on these
/// symbols; this test also catches text the analyzer cannot see, such as a package
/// reference to Dapper or a raw-SQL call hidden behind <c>dynamic</c>.
/// </summary>
public sealed class RawSqlBanTests
{
    // Built from fragments so this file does not match its own patterns.
    private static readonly string[] BannedTokens =
    [
        "FromSql" + "Raw", "FromSql" + "Interpolated", "FromSql" + "(", "ExecuteSql" + "Raw", "ExecuteSql" + "Interpolated",
        "ExecuteSql" + "(", "ExecuteSql" + "Async(", "SqlQuery" + "Raw", "SqlQuery" + "<", "Dap" + "per",
        "Npgsql" + "Command", "Sql" + "Command", "Db" + "Command(", "migrationBuilder" + ".Sql(", "HasDefaultValue" + "Sql",
        "HasComputedColumn" + "Sql", "HasFilter" + "(", "GetDb" + "Connection(", "Create" + "Command(",
    ];

    public static TheoryData<string> Tokens()
    {
        var data = new TheoryData<string>();
        foreach (var t in BannedTokens) data.Add(t);
        return data;
    }

    [Theory]
    [MemberData(nameof(Tokens))]
    public void No_source_file_uses_a_raw_sql_entry_point(string token)
    {
        var offenders = SolutionFiles("*.cs")
            .Where(f => !f.EndsWith(nameof(RawSqlBanTests) + ".cs", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: f, Line: i + 1, Text: line)))
            .Where(x => x.Text.Contains(token, StringComparison.Ordinal))
            .Select(x => $"{Path.GetRelativePath(SolutionRoot, x.File)}:{x.Line}: {x.Text.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0, $"Raw SQL entry point '{token}' found:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void No_project_references_a_micro_orm_or_ado_net_provider_directly()
    {
        var banned = new Regex(@"Include=""(Dapper[^""]*|Microsoft\.Data\.SqlClient|System\.Data\.SqlClient|RepoDb[^""]*|PetaPoco[^""]*)""");
        var offenders = SolutionFiles("*.csproj").Concat(SolutionFiles("*.props"))
            .Where(f => banned.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(SolutionRoot, f))
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Banned_symbol_list_is_wired_into_every_project()
    {
        var props = File.ReadAllText(Path.Combine(SolutionRoot, "Directory.Build.props"));
        Assert.Contains("Microsoft.CodeAnalysis.BannedApiAnalyzers", props);
        Assert.Contains("BannedSymbols.txt", props);
        var banned = File.ReadAllText(Path.Combine(SolutionRoot, "BannedSymbols.txt"));
        foreach (var name in new[] { "FromSqlRaw", "FromSqlInterpolated", "ExecuteSqlRaw", "ExecuteSqlInterpolated", "MigrationBuilder.Sql", "Dapper" })
        {
            Assert.Contains(name, banned);
        }
    }

    internal static string SolutionRoot { get; } = FindRoot();

    internal static IEnumerable<string> SolutionFiles(string pattern) =>
        Directory.EnumerateFiles(SolutionRoot, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EPTradeBot.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("EPTradeBot.sln not found above the test output directory.");
    }
}
