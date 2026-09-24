<!-- snippet: lockfile-test -->
```cs
public class EventContractTests
{
    [Fact]
    public void Event_contracts_are_stable() =>
        EventContracts.Verify(es => es.Stream<Cart>(s => s.Events<ItemAdded, CheckedOut>()), "events.lock");
}
```
<!-- endSnippet -->
