<!-- snippet: erase-subject -->
```cs
// Their data reads as erased as soon as this returns; the job finishes the rest.
var jobId = await erasure.EraseSubjectAsync("person:8421");
```
<!-- endSnippet -->
