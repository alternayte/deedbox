<!-- snippet: pseudonyms-write -->
```cs
// One secret per quarter: the same person gets a new subject ID each quarter.
var author = await pseudonyms.SubjectForAsync($"github:{login}", PseudonymPeriod.Quarter(clock.GetUtcNow()));
await store.Append(sessionId, ExpectedVersion.Any, [new CorrectionRecorded(author, text)]);
```
<!-- endSnippet -->
