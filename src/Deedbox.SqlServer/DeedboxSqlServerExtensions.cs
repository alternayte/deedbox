using Deedbox.SqlServer;

namespace Deedbox;

/// <summary>Chooses SQL Server as the Deedbox database.</summary>
public static class DeedboxSqlServerExtensions
{
    /// <summary>Stores events in SQL Server 2016 or later.</summary>
    /// <param name="builder">The Deedbox builder.</param>
    /// <param name="connectionString">A Microsoft.Data.SqlClient connection string.</param>
    public static DeedboxBuilder UseSqlServer(this DeedboxBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        builder.UseProvider(schema => new SqlServerProvider(connectionString, schema));
        return builder;
    }
}
