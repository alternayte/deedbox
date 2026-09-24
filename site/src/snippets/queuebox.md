<!-- snippet: queuebox -->
```cs
services.AddDeedbox(es => es
    .UsePostgres(connStr)
    .Keys(keys => keys.FromEnvironment("DEEDBOX_MASTER_KEY"))
    .Stream<Cart>(s => s.Events<ItemAdded, CheckedOut>())
    .Stream<Manuscript>(s => s.Events<ReviewerInvited, CoAuthorAdded>())
    .UseQueueBox(q => q
        .Publish<CheckedOut>("cart.checked_out")
        // Events with [PersonalData] need a payload you shape, so no personal data leaks by default.
        .Publish<ReviewerInvited>("review.invited", (e, info) => new { e.ManuscriptId, e.ReviewerId })
        // Tell downstream systems to erase too.
        .Publish<SubjectErased>("privacy.subject_erased")));
```
<!-- endSnippet -->
