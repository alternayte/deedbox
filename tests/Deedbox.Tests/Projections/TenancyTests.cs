using Deedbox.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Deedbox.Tests.Projections;

public sealed class PostgresTenancyTests(Databases databases) : TenancyTests(databases, Db.Postgres);

public sealed class SqlServerTenancyTests(Databases databases) : TenancyTests(databases, Db.SqlServer);

public abstract class TenancyTests(Databases databases, Db db) : StoreTest(databases, db)
{
    [Fact]
    public async Task The_same_stream_id_in_two_tenants_is_two_streams()
    {
        var services = await Services();
        var acme = ForTenant(services, "acme");
        var globex = ForTenant(services, "globex");

        await acme.Append("cart-1", ExpectedVersion.NoStream, [new ItemAdded("a", 1)]);
        var other = await globex.Append("cart-1", ExpectedVersion.NoStream, [new ItemAdded("b", 5), new ItemAdded("b", 5)]);

        Assert.Equal("globex", other.Events[0].TenantId);
        Assert.Equal((1L, 1), ((await acme.Load<Cart>("cart-1")).Version, (await acme.Load<Cart>("cart-1")).State.Items["a"]));
        Assert.Equal((2L, 10), ((await globex.Load<Cart>("cart-1")).Version, (await globex.Load<Cart>("cart-1")).State.Items["b"]));
        Assert.Equal(0, (await ForTenant(services, "").Load<Cart>("cart-1")).Version);
        Assert.Equal(2, await Scalar<int>($"SELECT COUNT(*) FROM {Table("streams")} WHERE stream_id = 'cart-1'"));
    }

    [Fact]
    public async Task A_bound_store_keeps_the_scope_tenant()
    {
        var services = await Services();
        await using var connection = await OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(Ct);

        var result = await ForTenant(services, "acme").UseTransaction(transaction).Append("cart-1", ExpectedVersion.NoStream, [new ItemAdded("a", 1)]);
        await transaction.CommitAsync(Ct);

        Assert.Equal("acme", result.Events[0].TenantId);
        Assert.Equal("acme", await Scalar<string>($"SELECT tenant_id FROM {Table("events")}"));
    }

    [Theory]
    [InlineData(" acme")]
    [InlineData("acme ")]
    public async Task Invalid_tenant_ids_are_rejected(string tenant)
    {
        var services = await Services();

        var error = await Assert.ThrowsAsync<DeedboxException>(() => ForTenant(services, tenant).Load<Cart>("cart-1"));

        Assert.Equal("DBX022", error.Code);
    }

    private static IEventStore ForTenant(IServiceProvider services, string tenant)
    {
        var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<DeedboxContext>().TenantId = tenant;
        return scope.ServiceProvider.GetRequiredService<IEventStore>();
    }
}
