<!-- snippet: publishing-register -->
```cs
builder.Services.AddDbContextFactory<PublishingDb>(o => o.UseNpgsql(connStr));  // also registers PublishingDb as scoped
builder.Services.AddSingleton<ManuscriptQueries>();
builder.Services.AddDeedbox(es => es
    .UsePostgres(connStr)
    .ApplySchemaOnStartup()
    .Keys(keys => keys.StoreInDatabase())  // AuthorAdded holds personal data
    .Stream<Manuscript>("manuscript", s => s
        .Events<ManuscriptStarted, SectionRevised, AuthorAdded, VersionFrozen, ReviewRoundOpened, DecisionMade>()
        .Events<Published, UpdateIssued>())
    .Projection<ManuscriptProjection>("manuscripts", Run.Inline));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));  // "Published", not 5
```
<!-- endSnippet -->
