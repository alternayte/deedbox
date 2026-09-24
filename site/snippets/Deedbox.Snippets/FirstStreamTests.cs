using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace Shop;

/// <summary>Runs the first-stream tutorial end to end against Postgres, so the tutorial cannot rot.</summary>
public sealed class FirstStreamTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task The_tutorial_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        Registration.Postgres(builder, _postgres.GetConnectionString());
        await using var app = builder.Build();
        await app.StartAsync(ct);

        using var scope = app.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
        await Writing.Execute(store, "cart-1", "apple", 2);
        await Writing.Explicit(store, "cart-1", DateTimeOffset.UnixEpoch);
        var (cart, version) = await store.Load<Cart>("cart-1");

        Assert.Equal((2, true, 2L), (cart.Items["apple"], cart.IsCheckedOut, version));
        await app.StopAsync(ct);
    }

    [Fact]
    public async Task The_inline_ef_projection_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        var connStr = _postgres.GetConnectionString();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddDbContext<ShopDb>(o => o.UseNpgsql(connStr));
        builder.Services.AddDeedbox(es => es
            .UsePostgres(connStr)
            .Schema("tutorial_ef")
            .ApplySchemaOnStartup()
            .Stream<Cart>(s => s.Events<ItemAdded, CheckedOut>())
            .Projection<CartSummaryProjection>("cart_summary", Run.Inline));
        await using var app = builder.Build();
        using (var setup = app.Services.CreateScope())
            await setup.ServiceProvider.GetRequiredService<ShopDb>().Database.EnsureCreatedAsync(ct);
        await app.StartAsync(ct);

        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IEventStore>().Execute<Cart>("cart-9", cart => CartDecider.Add(cart, "apple", 3), ct);

        var row = await scope.ServiceProvider.GetRequiredService<ShopDb>().CartSummaries.SingleAsync(ct);
        Assert.Equal(("cart-9", 3), (row.Id, row.ItemCount));
        await app.StopAsync(ct);
    }
}
