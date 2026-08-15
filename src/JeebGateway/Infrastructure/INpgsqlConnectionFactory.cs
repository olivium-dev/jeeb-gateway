using Npgsql;

namespace JeebGateway.Infrastructure;

/// <summary>
/// Creates open <see cref="NpgsqlConnection"/> instances bound to the
/// gateway-postgres connection string. Callers own the connection lifetime
/// (dispose after use). Implementations must not pool connections themselves —
/// Npgsql's built-in connection pool handles that at the driver level.
/// </summary>
public interface INpgsqlConnectionFactory
{
    /// <summary>Opens and returns a fresh database connection.</summary>
    Task<NpgsqlConnection> OpenAsync(CancellationToken ct);
}

/// <summary>
/// Default factory reading the connection string from configuration key
/// <c>GatewayPostgres:ConnectionString</c>.
/// </summary>
public sealed class NpgsqlConnectionFactory : INpgsqlConnectionFactory
{
    private readonly string _connectionString;

    public NpgsqlConnectionFactory(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Gateway Postgres connection string must be configured.", nameof(connectionString));
        _connectionString = connectionString;
    }

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
