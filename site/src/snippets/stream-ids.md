<!-- snippet: stream-ids -->
```cs
var fromGuid = StreamId.From(Guid.NewGuid());   // "0f8fad5b-d9cb-469f-a165-70867728950e"

var ns = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
var forPair = StreamId.Deterministic(ns, userId.ToString(), titleId.ToString()); // the same pair gives the same ID
```
<!-- endSnippet -->
