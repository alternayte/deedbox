<!-- snippet: schema-ef-migration -->
```cs
// An EF Core migration that creates the Deedbox tables without adding them to your model.
public partial class AddDeedbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(PostgresSchema.Script(fromVersion: 0));   // SqlServerSchema.Script on SQL Server
}
```
<!-- endSnippet -->
