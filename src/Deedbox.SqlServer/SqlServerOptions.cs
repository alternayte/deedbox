namespace Deedbox;

/// <summary>SQL Server storage options for <c>UseSqlServer(connectionString, o =&gt; ...)</c>.</summary>
public sealed class SqlServerOptions
{
    /// <summary>
    /// Stores events, state, checkpoint errors and job data in the native json type instead of nvarchar(max). It needs
    /// SQL Server 2025, Azure SQL Database or Azure SQL Managed Instance. Applying the schema converts existing
    /// columns, which rewrites their rows; start-up fails with DBX034 until the columns are converted. Off by default.
    /// </summary>
    public bool NativeJson { get; set; }
}
