<!-- snippet: archive-streams -->
```cs
// The app picks the finished streams from its own read model, in batches of the size it chooses.
var archive = await admin.ArchiveStreamsAsync([.. finishedManuscripts.Select(id => new StreamKey("", id))]);

// A rebuild skips archived events; this one replays them too.
var rebuild = await admin.RebuildWithArchiveAsync("manuscript_report");
```
<!-- endSnippet -->
