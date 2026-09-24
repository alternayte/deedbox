using System.Text.Json;
using Deedbox.Tests.Infrastructure;
using Deedbox.Tests.PersonalData;
using Deedbox.Tests.Runner;
using Microsoft.Extensions.DependencyInjection;

namespace Deedbox.Tests.QueueBox;

public sealed class PostgresQueueBoxTests(Databases databases) : QueueBoxTests(databases, Db.Postgres);

public sealed class SqlServerQueueBoxTests(Databases databases) : QueueBoxTests(databases, Db.SqlServer);

public abstract class QueueBoxTests(Databases databases, Db db) : RunnerTest(databases, db)
{
    [Fact]
    public async Task Published_events_become_outbox_rows_in_the_append_transaction()
    {
        var host = await StartHost(NewProbe(), b => b.UseQueueBox(q => q.UseTable("outbox", Schema).Publish<CheckedOut>("cart.checked_out")));
        await CreateOutbox("outbox", QueueBoxDdl);
        var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<DeedboxContext>().Metadata = new EventMetadata { CorrelationId = "req-7" };
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();

        var result = await store.Append("cart-1", ExpectedVersion.NoStream, [new ItemAdded("a", 1), new CheckedOut(DateTimeOffset.UnixEpoch)]);
        await using (var connection = await OpenConnection())
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await store.UseTransaction(transaction).Append("cart-2", ExpectedVersion.NoStream, [new CheckedOut(DateTimeOffset.UnixEpoch)]);
            await transaction.RollbackAsync(Ct);
        }

        var row = Assert.Single(await Rows("outbox", "id", "topic", "key", "payload", "headers", "aggregate_type", "state", "attempt"));
        Assert.Equal(result.Events[1].EventId, Guid.Parse(row["id"]));
        Assert.Equal(("cart.checked_out", "cart-1", "cart", "pending", "0"), (row["topic"], row["key"], row["aggregate_type"], row["state"], row["attempt"]));
        Assert.Equal("1970-01-01T00:00:00+00:00", JsonDocument.Parse(row["payload"]).RootElement.GetProperty("at").GetString());
        var headers = JsonDocument.Parse(row["headers"]).RootElement;
        Assert.Equal(("req-7", "cart.checked_out", "cart-1", "2"),
            (headers.GetProperty("X-Correlation-Id").GetString(), headers.GetProperty("x-deedbox-event-type").GetString(),
             headers.GetProperty("x-deedbox-stream-id").GetString(), headers.GetProperty("x-deedbox-stream-version").GetString()));
    }

    [Fact]
    public async Task A_message_callback_shapes_topic_payload_and_headers_per_event_and_can_skip_one()
    {
        var host = await StartHost(NewProbe(), b => b.UseQueueBox(q => q.UseTable("outbox", Schema).Publish<ItemAdded>((e, p) => e.Sku == "skip"
            ? null
            : new QueueBoxMessage($"cart.{p.StreamId}", new { specversion = "1.0", id = p.EventId, type = p.EventType, data = e })
            {
                Headers = new Dictionary<string, string>
                {
                    ["ce-type"] = p.EventType,
                    ["x-correlation-id"] = "override",
                    ["tenant"] = p.Metadata.Headers["tenant"],
                },
            })));
        await CreateOutbox("outbox", QueueBoxDdl);
        var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<DeedboxContext>().Metadata =
            new EventMetadata { CorrelationId = "req-7", Headers = new Dictionary<string, string> { ["tenant"] = "acme" } };

        var result = await scope.ServiceProvider.GetRequiredService<IEventStore>()
            .Append("cart-1", ExpectedVersion.NoStream, [new ItemAdded("a", 1), new ItemAdded("skip", 1)]);

        var row = Assert.Single(await Rows("outbox", "id", "topic", "key", "payload", "headers"));
        var added = result.Events[0];
        Assert.Equal((added.EventId, "cart.cart-1", "cart-1"), (Guid.Parse(row["id"]), row["topic"], row["key"]));
        var payload = JsonDocument.Parse(row["payload"]).RootElement;
        Assert.Equal((added.EventType, "a"), (payload.GetProperty("type").GetString(), payload.GetProperty("data").GetProperty("sku").GetString()));
        var headers = JsonDocument.Parse(row["headers"]).RootElement.EnumerateObject().ToDictionary(h => h.Name, h => h.Value.GetString());
        Assert.Equal((added.EventType, "acme", added.EventId.ToString("D")), (headers["ce-type"], headers["tenant"], headers["x-deedbox-event-id"]));
        Assert.Equal("override", Assert.Single(headers, h => string.Equals(h.Key, "X-Correlation-Id", StringComparison.OrdinalIgnoreCase)).Value);
    }

    [Fact]
    public async Task An_invalid_message_or_a_failing_callback_fails_the_append_with_DBX032_and_writes_nothing()
    {
        var host = await StartHost(NewProbe(), b => b.UseQueueBox(q => q.UseTable("outbox", Schema).Publish<ItemAdded>((e, _) => e.Sku switch
        {
            "empty" => new QueueBoxMessage(" ", e),
            "long" => new QueueBoxMessage(new string('t', 256), e),
            "no-value" => new QueueBoxMessage("cart.item_added", e) { Headers = new Dictionary<string, string> { ["h"] = null! } },
            _ => throw new InvalidOperationException("boom"),
        })));
        await CreateOutbox("outbox", QueueBoxDdl);

        foreach (var sku in new[] { "empty", "long", "no-value", "throws" })
        {
            var error = await Assert.ThrowsAsync<DeedboxException>(() => StoreOf(host).Append($"cart-{sku}", ExpectedVersion.NoStream, [new ItemAdded(sku, 1)]));
            Assert.Equal((sku, "DBX032"), (sku, error.Code));
            if (sku == "throws")
                Assert.IsType<InvalidOperationException>(error.InnerException);
        }

        Assert.Empty(await Rows("outbox", "id"));
        Assert.Equal(0, await Scalar<int>($"SELECT COUNT(*) FROM {Table("events")}"));
    }

    [Fact]
    public async Task Personal_data_is_published_only_through_a_payload_mapping_and_erasure_is_forwarded()
    {
        var error = Assert.Throws<DeedboxException>(() => new ServiceCollection().AddDeedbox(b => Configure(UseDatabase(b))
            .UseQueueBox(q => q.Publish<ReviewerInvited>("review.invited"))));
        Assert.Equal("DBX032", error.Code);
        Assert.Contains("plain text", error.Message, StringComparison.Ordinal);
        new ServiceCollection().AddDeedbox(b => Configure(UseDatabase(b))
            .UseQueueBox(q => q.Publish<ReviewerInvited>((e, _) => new QueueBoxMessage("review.invited", new { e.ManuscriptId }))));

        var host = await StartHost(NewProbe(), b => Configure(b).UseQueueBox(q => q
            .UseTable("outbox", Schema)
            .Publish<ReviewerInvited>("review.invited", (e, info) => new { e.ManuscriptId, e.ReviewerId, Stream = info.StreamId, info.Version })
            .Publish<SubjectErased>("privacy.subject_erased")));
        await CreateOutbox("outbox", QueueBoxDdl);
        await StoreOf(host).Append("m-1", ExpectedVersion.NoStream, [new ReviewerInvited("m-1", "person:1", "Ada", "ada@example.org")]);
        var job = await host.Services.CreateScope().ServiceProvider.GetRequiredService<ISubjectErasure>().EraseSubjectAsync("person:1");
        await WaitForJob(host, job);

        var rows = await Rows("outbox", "topic", "payload");
        Assert.Equal(["privacy.subject_erased", "review.invited"], rows.Select(r => r["topic"]).Order());
        Assert.DoesNotContain("Ada", string.Join(' ', rows.Select(r => r["payload"])), StringComparison.Ordinal);
        var invited = JsonDocument.Parse(rows.Single(r => r["topic"] == "review.invited")["payload"]).RootElement;
        Assert.Equal(("m-1", 1L), (invited.GetProperty("stream").GetString(), invited.GetProperty("version").GetInt64()));
        Assert.Equal("person:1", JsonDocument.Parse(rows.Single(r => r["topic"] == "privacy.subject_erased")["payload"]).RootElement.GetProperty("subjectId").GetString());
    }

    [Fact]
    public async Task A_renamed_outbox_table_and_columns_are_written()
    {
        var host = await StartHost(NewProbe(), b => b.UseQueueBox(q => q
            .UseTable("messages", Schema)
            .UseColumns(c => (c.Id, c.Topic, c.Payload) = ("message_id", "event_topic", "event_data"))
            .Publish<ItemAdded>("cart.item_added")));
        await CreateOutbox("messages", QueueBoxDdl.Replace("id UUID", "message_id UUID").Replace("id UNIQUEIDENTIFIER", "message_id UNIQUEIDENTIFIER")
            .Replace("topic ", "event_topic ").Replace("payload ", "event_data "));

        await StoreOf(host).Append("cart-1", ExpectedVersion.NoStream, [new ItemAdded("a", 1)]);

        Assert.Equal("cart.item_added", Assert.Single(await Rows("messages", "event_topic"))["event_topic"]);
    }

    [Fact]
    public void Unregistered_or_duplicate_publications_fail_at_startup()
    {
        var unregistered = Assert.Throws<DeedboxException>(() => new ServiceCollection().AddDeedbox(b => UseDatabase(b)
            .Stream<Cart>(s => s.Events<ItemAdded>())
            .UseQueueBox(q => q.Publish<OrderPlaced>("order.placed"))));
        var twice = Assert.Throws<DeedboxException>(() => new ServiceCollection().AddDeedbox(b => UseDatabase(b)
            .Stream<Cart>(s => s.Events<ItemAdded>())
            .UseQueueBox(q => q.Publish<ItemAdded>("a").Publish<ItemAdded>("b"))));
        var badTable = Assert.Throws<DeedboxException>(() => new ServiceCollection().AddDeedbox(b => UseDatabase(b)
            .Stream<Cart>(s => s.Events<ItemAdded>())
            .UseQueueBox(q => q.UseTable("outbox; drop table x"))));

        Assert.Equal(("DBX032", "DBX032", "DBX032"), (unregistered.Code, twice.Code, badTable.Code));
    }

    private static DeedboxBuilder Configure(DeedboxBuilder b)
    {
        Keys.Streams(b);
        return b.Keys(k => k.StoreInDatabase());
    }

    /// <summary>QueueBox's shipped outbox table: migration V1 plus V9 (aggregate_type), in the test schema.</summary>
    private string QueueBoxDdl => Db == Db.Postgres
        ? """
          CREATE TABLE {table} (
              id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
              topic VARCHAR(255) NOT NULL,
              key VARCHAR(255),
              payload JSONB NOT NULL,
              headers JSONB NOT NULL DEFAULT '{}',
              state VARCHAR(50) NOT NULL DEFAULT 'pending',
              attempt INTEGER NOT NULL DEFAULT 0,
              max_attempts INTEGER NOT NULL DEFAULT 5,
              scheduled_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
              created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
              updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
              aggregate_type VARCHAR(255)
          )
          """
        : """
          CREATE TABLE {table} (
              id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
              topic NVARCHAR(255) NOT NULL,
              [key] NVARCHAR(255),
              payload NVARCHAR(MAX) NOT NULL,
              headers NVARCHAR(MAX) NOT NULL DEFAULT '{}',
              state NVARCHAR(50) NOT NULL DEFAULT 'pending',
              attempt INT NOT NULL DEFAULT 0,
              max_attempts INT NOT NULL DEFAULT 5,
              scheduled_at DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
              created_at DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
              updated_at DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
              aggregate_type NVARCHAR(255)
          )
          """;

    private async Task CreateOutbox(string name, string ddl)
    {
        await using var connection = await OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = ddl.Replace("{table}", Table(name), StringComparison.Ordinal);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<List<Dictionary<string, string>>> Rows(string table, params string[] columns)
    {
        await using var connection = await OpenConnection();
        await using var command = connection.CreateCommand();
        var quoted = columns.Select(c => Db == Db.Postgres ? $"\"{c}\"" : $"[{c}]");
        command.CommandText = $"SELECT {string.Join(", ", quoted.Select(c => Db == Db.Postgres ? $"CAST({c} AS text)" : $"CAST({c} AS nvarchar(max))"))} FROM {Table(table)}";
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<Dictionary<string, string>>();
        while (await reader.ReadAsync(Ct))
            rows.Add(columns.Select((c, i) => (c, reader.GetString(i))).ToDictionary(p => p.c, p => p.Item2));
        return rows;
    }
}
