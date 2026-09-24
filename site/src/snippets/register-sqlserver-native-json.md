<!-- snippet: register-sqlserver-native-json -->
```cs
builder.Services.AddDeedbox(es => es
    .UseSqlServer(connStr, sql => sql.NativeJson = true) // SQL Server 2025 or Azure SQL
    .ApplySchemaOnStartup()                             // converts nvarchar(max) columns to json
    .Stream<Cart>(s => s.Events<ItemAdded, CheckedOut>()));
```
<!-- endSnippet -->
