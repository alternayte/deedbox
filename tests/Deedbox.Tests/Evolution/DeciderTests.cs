using Deedbox.Testing;
using Deedbox.Tests.Infrastructure;

namespace Deedbox.Tests.Evolution;

public sealed class DeciderTests
{
    private static IEnumerable<object> CheckOut(Cart cart) =>
        cart.IsCheckedOut ? throw new InvalidOperationException("Already checked out.")
        : cart.Items.IsEmpty ? []
        : [new CheckedOut(DateTimeOffset.UnixEpoch)];

    [Fact]
    public void Given_folds_the_events_and_then_matches_the_decision()
    {
        var scenario = Decider.Given<Cart>(new ItemAdded("a", 1), new ItemAdded("a", 2));

        Assert.Equal(3, scenario.State.Items["a"]);
        scenario.When(CheckOut).Then(new CheckedOut(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Then_fails_with_expected_and_actual_events()
    {
        var error = Assert.Throws<DeciderAssertionException>(() =>
            Decider.Given<Cart>(new ItemAdded("a", 1)).When(CheckOut).Then(new CheckedOut(DateTimeOffset.MaxValue)));

        Assert.Contains("Expected events:", error.Message, StringComparison.Ordinal);
        Assert.Contains("CheckedOut {\"at\":\"9999", error.Message, StringComparison.Ordinal);
        Assert.Contains("CheckedOut {\"at\":\"1970", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThenNothing_and_ThenThrows_check_empty_and_failed_decisions()
    {
        Decider.Given<Cart>().When(CheckOut).ThenNothing();
        var thrown = Decider.Given<Cart>(new ItemAdded("a", 1), new CheckedOut(DateTimeOffset.UnixEpoch)).When(CheckOut).ThenThrows<InvalidOperationException>();

        Assert.Equal("Already checked out.", thrown.Message);
        Assert.Throws<DeciderAssertionException>(() => Decider.Given<Cart>().When(CheckOut).ThenThrows<InvalidOperationException>());
        Assert.Throws<DeciderAssertionException>(() => Decider.Given<Cart>(new ItemAdded("a", 1), new CheckedOut(DateTimeOffset.UnixEpoch)).When(CheckOut).ThenNothing());
    }

    [Fact]
    public void Events_with_collections_compare_by_content()
    {
        Decider.Given<Cart>().When(_ => [new Painted(Colour.Red, ["base"], new() { ["x"] = 1m }, null, new ItemAddedV2("a", 1))])
            .Then(new Painted(Colour.Red, ["base"], new() { ["x"] = 1m }, null, new ItemAddedV2("a", 1)));
    }
}
