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
        builder.UseProvider(schema => new PostgresProvider(NpgsqlDataSource.Create(connectionString), ownsDataSource: true, schema));
        return builder;
    }

    /// <summary>Stores events in Postgres through a data source the app owns.</summary>
    /// <param name="builder">The Deedbox builder.</param>
    /// <param name="dataSource">The app's data source. Deedbox does not dispose it.</param>
    public static DeedboxBuilder UsePostgres(this DeedboxBuilder builder, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSource);
        builder.UseProvider(schema => new PostgresProvider(dataSource, ownsDataSource: false, schema));
        return builder;
    }
}
