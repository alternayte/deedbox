using System.Data.Common;
using Deedbox;

namespace MyApp;

/// <summary>An inline projection: a cart_summary row per cart, written in the same transaction as the events.</summary>
public sealed class CartSummary : Projection
{
    public CartSummary()
    {
        On<ItemAdded>((e, ctx) => Execute(ctx.Connection, ctx.Transaction,
            Sql.Upsert, ("id", ctx.StreamId), ("qty", e.Qty)));
        On<CheckedOut>((_, ctx) => Execute(ctx.Connection, ctx.Transaction,
            "UPDATE cart_summary SET checked_out = 1 WHERE id = @id", ("id", ctx.StreamId)));
    }

    protected override Task ResetAsync(WriteContext context) =>
        Execute(context.Connection, context.Transaction, "DELETE FROM cart_summary");

    public static async Task CreateTable(DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Sql.Create;
        await command.ExecuteNonQueryAsync();
    }

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

    private static class Sql
    {
#if (postgres)
        public const string Create = "CREATE TABLE IF NOT EXISTS cart_summary (id text PRIMARY KEY, items int NOT NULL, checked_out int NOT NULL DEFAULT 0)";
        public const string Upsert = "INSERT INTO cart_summary (id, items) VALUES (@id, @qty) ON CONFLICT (id) DO UPDATE SET items = cart_summary.items + @qty";
#else
        public const string Create = "IF OBJECT_ID('cart_summary') IS NULL CREATE TABLE cart_summary (id nvarchar(200) PRIMARY KEY, items int NOT NULL, checked_out int NOT NULL DEFAULT 0)";
        public const string Upsert = "MERGE cart_summary AS t USING (SELECT @id AS id) AS s ON t.id = s.id WHEN MATCHED THEN UPDATE SET items = t.items + @qty WHEN NOT MATCHED THEN INSERT (id, items) VALUES (@id, @qty);";
#endif
    }
}
