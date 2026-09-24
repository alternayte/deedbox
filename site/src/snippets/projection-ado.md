<!-- snippet: projection-ado -->
```cs
// ADO.NET or Dapper flavour: write through ctx.Connection and ctx.Transaction.
public sealed class CartTotals : Projection
{
    public CartTotals()
    {
        On<ItemAdded>((e, ctx) => Execute(ctx.Connection, ctx.Transaction,
            "UPDATE cart_totals SET items = items + @qty WHERE cart_id = @cart",
            ("qty", e.Qty), ("cart", ctx.StreamId)));
    }

    protected override Task ResetAsync(WriteContext context) =>
        Execute(context.Connection, context.Transaction, "DELETE FROM cart_totals");

    private static async Task Execute(DbConnection connection, DbTransaction transaction, string sql, params (string Name, object Value)[] values)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }
}
```
<!-- endSnippet -->
