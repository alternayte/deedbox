using Deedbox.Postgres;
using Npgsql;

namespace Deedbox;

/// <summary>Chooses Postgres as the Deedbox database.</summary>
public static class DeedboxPostgresExtensions
{
    /// <summary>Stores events in Postgres. Deedbox builds and owns a data source for the connection string.</summary>
    /// <param name="builder">The Deedbox builder.</param>
    /// <param name="connectionString">An Npgsql connection string.</param>
    public static DeedboxBuilder UsePostgres(this DeedboxBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        builder.UseProvider((schema, _) => new PostgresProvider(NpgsqlDataSource.Create(connectionString), ownsDataSource: true, schema));
        return builder;
    }

    /// <summary>
    /// Stores events in Postgres, with a connection string that Deedbox reads from the app's services when it starts,
    /// such as <c>sp =&gt; sp.GetRequiredService&lt;IConfiguration&gt;().GetConnectionString("Default")!</c>. Use it when
    /// configuration is final only after registration, as under WebApplicationFactory. Deedbox owns the data source.
    /// </summary>
    /// <param name="builder">The Deedbox builder.</param>
    /// <param name="connectionString">Returns an Npgsql connection string.</param>
    public static DeedboxBuilder UsePostgres(this DeedboxBuilder builder, Func<IServiceProvider, string> connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connectionString);
        builder.UseProvider((schema, services) => new PostgresProvider(NpgsqlDataSource.Create(Required(connectionString(services))), ownsDataSource: true, schema));
        return builder;
    }

    /// <summary>Stores events in Postgres through a data source the app owns.</summary>
    /// <param name="builder">The Deedbox builder.</param>
    /// <param name="dataSource">The app's data source. Deedbox does not dispose it.</param>
    public static DeedboxBuilder UsePostgres(this DeedboxBuilder builder, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSource);
        builder.UseProvider((schema, _) => new PostgresProvider(dataSource, ownsDataSource: false, schema));
        return builder;
    }

    private static string Required(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        return connectionString;
    }
}
