using Deedbox.Testing;
using Deedbox.Testing.Contracts;
using Deedbox.Tests.Infrastructure;

namespace Deedbox.Tests.Evolution;

public record ItemAddedWithNote(string Sku, int Qty, string? Note);

public record ItemAddedWithCode(string Sku, int Qty, int Code);

public record ItemAddedWithoutQty(string Sku);

public record ItemAddedQtyText(string Sku, string Qty);

public record PlainInvited(string ManuscriptId, string ReviewerId, string ReviewerName, string? ReviewerEmail);

public enum Colour
{
    Red,
    Green,
}

public record Painted(Colour Colour, IReadOnlyList<string> Coats, Dictionary<string, decimal?> Prices, Guid? By, ItemAddedV2 Nested);

public sealed class EventContractsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"deedbox-{Guid.NewGuid():N}.lock");

    private static Action<DeedboxBuilder> Original => b => b.Stream<Cart>(s => s.Events<ItemAdded, CheckedOut>());

    [Fact]
    public void The_lockfile_records_streams_events_versions_aliases_and_shapes()
    {
        Assert.Throws<EventContractException>(() => EventContracts.Verify(b => b.Stream<Cart>(s => s
            .Event<ItemAdded>(version: 2, up => up.From(1, _ => { }).Alias("cart.line_added"))
            .Events<CheckedOut>()), _path, onCi: false));

        Assert.Equal(
            """
            # Deedbox event contracts. EventContracts.Verify writes this file; commit it.

            stream cart (Cart)
              cart.checked_out v1 { at: date-time }
              cart.item_added v2 { qty: int32, sku: string }
                alias cart.line_added

            """.ReplaceLineEndings("\n"),
            File.ReadAllText(_path));
    }

    [Fact]
    public void A_new_lockfile_is_written_and_fails_once_then_passes()
    {
        var created = Assert.Throws<EventContractException>(() => EventContracts.Verify(Original, _path, onCi: false));

        Assert.Contains("Created the event contract lockfile", created.Message, StringComparison.Ordinal);
        EventContracts.Verify(Original, _path, onCi: false);
    }

    [Fact]
    public void Under_ci_a_missing_or_outdated_lockfile_fails_without_writing()
    {
        Assert.Throws<EventContractException>(() => EventContracts.Verify(Original, _path, onCi: true));
        Assert.False(File.Exists(_path));

        Create(Original);
        var outdated = Assert.Throws<EventContractException>(() =>
            EventContracts.Verify(b => b.Stream<Cart>(s => s.Events<ItemAdded, CheckedOut>().Events<LineAdded>()), _path, onCi: true));
        Assert.Contains("out of date", outdated.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compatible_changes_rewrite_the_lockfile()
    {
        Create(Original);

        EventContracts.Verify(b => b.Stream<Cart>(s => s
            .Event<ItemAddedWithNote>("cart.item_added")
            .Events<CheckedOut, LineAdded>()), _path, onCi: false);

        var text = File.ReadAllText(_path);
        Assert.Contains("cart.item_added v1 { note: string?, qty: int32, sku: string }", text, StringComparison.Ordinal);
        Assert.Contains("cart.line_added v1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_removed_event_breaks_unless_an_alias_keeps_its_name()
    {
        Create(Original);

        var removed = Assert.Throws<EventContractException>(() => EventContracts.Verify(b => b.Stream<Cart>(s => s.Events<LineAdded, CheckedOut>()), _path, onCi: false));
        Assert.Contains("'cart.item_added' was removed", removed.Message, StringComparison.Ordinal);

        EventContracts.Verify(b => b.Stream<Cart>(s => s.Event<LineAdded>(e => e.Alias("cart.item_added")).Events<CheckedOut>()), _path, onCi: false);
        Assert.Contains("    alias cart.item_added", File.ReadAllText(_path), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(ItemAddedWithCode), "'code' was added as non-nullable")]
    [InlineData(typeof(ItemAddedWithoutQty), "'qty' was removed")]
    [InlineData(typeof(ItemAddedQtyText), "'qty' changed from int32 to string")]
    public void Incompatible_shape_changes_without_a_new_version_break(Type changed, string problem)
    {
        Create(Original);

        var error = Assert.Throws<EventContractException>(() => EventContracts.Verify(b => b.Stream<Cart>(s =>
        {
            if (changed == typeof(ItemAddedWithCode))
                s.Event<ItemAddedWithCode>("cart.item_added");
            else if (changed == typeof(ItemAddedWithoutQty))
                s.Event<ItemAddedWithoutQty>("cart.item_added");
            else
                s.Event<ItemAddedQtyText>("cart.item_added");
            s.Events<CheckedOut>();
        }), _path, onCi: false));

        Assert.Contains(problem, error.Message, StringComparison.Ordinal);
        Assert.Contains("Register it as version 2 with an upcaster from version 1.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shape_change_with_a_new_version_and_upcaster_passes()
    {
        Create(Original);

        EventContracts.Verify(b => b.Stream<Cart>(s => s
            .Event<ItemAddedWithCode>(version: 2, up => up.Name("cart.item_added").From(1, json => json["code"] ??= 0))
            .Events<CheckedOut>()), _path, onCi: false);

        Assert.Contains("cart.item_added v2 { code: int32, qty: int32, sku: string }", File.ReadAllText(_path), StringComparison.Ordinal);
    }

    [Fact]
    public void The_lockfile_marks_personal_data_and_removing_a_marker_breaks()
    {
        Action<DeedboxBuilder> personal = b => b.Keys(k => k.StoreInDatabase()).Stream<Deedbox.Tests.PersonalData.Manuscript>(s => s.Event<Deedbox.Tests.PersonalData.ReviewerInvited>("m.invited"));
        Create(personal);

        Assert.Contains("m.invited v1 { manuscriptId: string, reviewerEmail: string? pd(reviewerId), reviewerId: string, reviewerName: string pd(reviewerId) }",
            File.ReadAllText(_path), StringComparison.Ordinal);
        var error = Assert.Throws<EventContractException>(() => EventContracts.Verify(
            b => b.Stream<Deedbox.Tests.PersonalData.Manuscript>(s => s.Event<PlainInvited>(version: 2, up => up.Name("m.invited").From(1, _ => { }))), _path, onCi: false));
        Assert.Contains("'reviewerName' is no longer [PersonalData]", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Shapes_print_and_parse_back_unchanged()
    {
        var shape = Shape.Of(typeof(Painted), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        });
        var text = shape.ToString();

        Assert.Equal("{ by: uuid?, coats: [string], colour: enum{Red=0, Green=1}, nested: { qty: int32, sku: string }, prices: map<decimal?> }", text);
        Assert.Equal(text, Shape.Parse(text).ToString());
        Assert.Equal("{ \"odd name\": int32? }", Shape.Parse("{ \"odd name\": int32? }").ToString());
    }

    private void Create(Action<DeedboxBuilder> configure)
    {
        Assert.Throws<EventContractException>(() => EventContracts.Verify(configure, _path, onCi: false));
    }

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }
}

public sealed class EventContractsPublicTests
{
    [Fact]
    public void Verify_resolves_the_lockfile_next_to_the_calling_source_file()
    {
        var name = $"deedbox-{Guid.NewGuid():N}.lock";
        var expected = Path.Combine(EventContracts.CallerDirectory(ThisFile()), name);
        try
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Throws<EventContractException>(() => EventContracts.Verify(b => b.Stream<Cart>(s => s.Events<ItemAdded>()), name));
                Assert.True(File.Exists(expected));
                EventContracts.Verify(b => b.Stream<Cart>(s => s.Events<ItemAdded>()), name);
            }
            else
            {
                var error = Assert.Throws<EventContractException>(() => EventContracts.Verify(b => b.Stream<Cart>(s => s.Events<ItemAdded>()), name));
                Assert.Contains(expected, error.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (File.Exists(expected))
                File.Delete(expected);
        }
    }

    [Fact]
    public void A_mapped_ci_source_path_resolves_by_walking_up_from_the_working_directory()
    {
        var root = Directory.CreateTempSubdirectory("deedbox-repo-");
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "tests", "App.Tests"));
            var working = Directory.CreateDirectory(Path.Combine(folder.FullName, "bin", "Debug"));

            Assert.Equal(folder.FullName, EventContracts.CallerDirectory("/_/tests/App.Tests/ContractTests.cs", working.FullName));
            Assert.Throws<EventContractException>(() => EventContracts.CallerDirectory("/_/nowhere/ContractTests.cs", working.FullName));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
