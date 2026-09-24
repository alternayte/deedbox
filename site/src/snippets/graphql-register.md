<!-- snippet: graphql-register -->
```cs
builder.Services.AddGraphQLServer()
    .AddQueryType<ManuscriptQuery>()
    .AddTypeExtension<ManuscriptSummaryFields>()
    .AddDataLoader<DocumentByIdDataLoader>()
    .AddDataLoader<VersionsByManuscriptIdDataLoader>();
```
<!-- endSnippet -->
