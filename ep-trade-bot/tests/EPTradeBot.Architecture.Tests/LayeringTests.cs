namespace EPTradeBot.Architecture.Tests;

public sealed class LayeringTests
{
    [Fact]
    public void Core_does_not_depend_on_ef_core_or_aspnet()
    {
        var refs = typeof(EPTradeBot.Core.Entities.Core.BrokerAccount).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(refs, r => r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(refs, r => r.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.DoesNotContain(refs, r => r.StartsWith("Npgsql", StringComparison.Ordinal));
    }
}
