using Deedbox.SqlServer;

namespace Deedbox;

/// <summary>Chooses SQL Server as the Deedbox database.</summary>
public static class DeedboxSqlServerExtensions
{
    /// <summary>Stores events in SQL Server 2016 or later.</summary>
    /// <param name="builder">The Deedbox builder.</param>
    /// <param name="connectionString">A Microsoft.Data.SqlClient connection string.</param>
    public static DeedboxBuilder UseSqlServer(this DeedboxBuilder builder, string connectionString) =>
        UseSqlServer(builder, connectionString, _ => { });

    /// <summary>Stores events in SQL Server 2016 or later, with storage options such as native json columns.</summary>
    /// <param name="builder">The Deedbox builder.</param>
    /// <param name="connectionString">A Microsoft.Data.SqlClient connection string.</param>
    /// <param name="configure">Sets the storage options.</param>
    public static DeedboxBuilder UseSqlServer(this DeedboxBuilder builder, string connectionString, Action<SqlServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        var options = Options(configure);
        builder.UseProvider((schema, _) => new SqlServerProvider(connectionString, schema, options.NativeJson));
        return builder;
    }

    /// <summary>
    /// Stores events in SQL Server 2016 or later, with a connection string that Deedbox reads from the app's services
    /// when it starts. Use it when configuration is final only after registration, as under WebApplicationFactory.
    /// </summary>
    /// <param name="builder">The Deedbox builder.</param>
    /// <param name="connectionString">Returns a Microsoft.Data.SqlClient connection string.</param>
    public static DeedboxBuilder UseSqlServer(this DeedboxBuilder builder, Func<IServiceProvider, string> connectionString) =>
        UseSqlServer(builder, connectionString, _ => { });

    /// <summary>
    /// Stores events in SQL Server 2016 or later, with a connection string that Deedbox reads from the app's services
    /// when it starts, and with storage options such as native json columns.
    /// </summary>
    /// <param name="builder">The Deedbox builder.</param>
    /// <param name="connectionString">Returns a Microsoft.Data.SqlClient connection string.</param>
    /// <param name="configure">Sets the storage options.</param>
    public static DeedboxBuilder UseSqlServer(this DeedboxBuilder builder, Func<IServiceProvider, string> connectionString, Action<SqlServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connectionString);
        var options = Options(configure);
        builder.UseProvider((schema, services) =>
        {
            var resolved = connectionString(services);
            ArgumentException.ThrowIfNullOrEmpty(resolved, nameof(connectionString));
            return new SqlServerProvider(resolved, schema, options.NativeJson);
        });
        return builder;
    }

    private static SqlServerOptions Options(Action<SqlServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new SqlServerOptions();
        configure(options);
        return options;
    }
}
