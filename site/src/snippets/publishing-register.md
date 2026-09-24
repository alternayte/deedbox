<!-- snippet: publishing-register -->
```cs
builder.Services.AddDbContext<PublishingDb>(o => o.UseNpgsql(connStr));
builder.Services.AddScoped<ManuscriptQueries>();
builder.Services.AddDeedbox(es => es
    .UsePostgres(connStr)
    .ApplySchemaOnStartup()
    .Keys(keys => keys.StoreInDatabase())  // AuthorAdded holds personal data
    .Stream<Manuscript>("manuscript", s => s
        .Events<ManuscriptStarted, SectionRevised, AuthorAdded, VersionFrozen, ReviewRoundOpened, DecisionMade>()
        .Events<Published, UpdateIssued>())
    .Projection<ManuscriptProjection>("manuscripts", Run.Inline));
builder.Services.AddGraphQLServer().AddQueryType<ManuscriptQuery>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));  // "Published", not 5
```
<!-- endSnippet -->
