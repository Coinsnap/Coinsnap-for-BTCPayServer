using Npgsql;

namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

/// <summary>
/// Starts every run from an empty database, so the bootstrap user is always the first account and
/// therefore the server admin. Only the gate's own database is touched.
/// </summary>
public static class GateDatabase
{
    public static async Task ResetAsync()
    {
        var target = new NpgsqlConnectionStringBuilder(GateConfig.PostgresConnectionString);
        var database = target.Database
                       ?? throw new InvalidOperationException("The gate's Postgres connection string names no database.");
        if (!database.Contains("releasegate", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to drop '{database}': it is not a release-gate database.");

        var admin = new NpgsqlConnectionStringBuilder(target.ConnectionString) { Database = "postgres" };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>Removes a store's amount limits and the plugin's record of them, as if never set. BTCPay must be stopped.</summary>
    public static async Task ClearAmountLimitsAsync(string storeId)
    {
        await using var connection = new NpgsqlConnection(GateConfig.PostgresConnectionString);
        await connection.OpenAsync();
        await using var update = new NpgsqlCommand(
            "UPDATE \"Stores\" SET \"StoreBlob\" = \"StoreBlob\" - 'paymentMethodCriteria' - 'PaymentMethodCriteria' - 'coinsnapAmountLimits' WHERE \"Id\" = @id",
            connection);
        update.Parameters.AddWithValue("id", storeId);
        if (await update.ExecuteNonQueryAsync() != 1)
            throw new InvalidOperationException($"Store {storeId} was not found in the gate database.");
    }
}
