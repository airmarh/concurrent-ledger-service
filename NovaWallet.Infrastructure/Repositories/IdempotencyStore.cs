using Microsoft.EntityFrameworkCore;
using NovaWallet.Domain;

namespace NovaWallet.Infrastructure;

public class IdempotencyStore(NovaWalletDbContext db) : IIdempotencyStore
{
    public async Task<IdempotencyClaim> ClaimAsync(string customerId, string key, string requestHash, CancellationToken ct = default)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.CommandText =
                """
                INSERT INTO idempotency_keys (customer_id, key, request_hash, status, created_at)
                VALUES (@customerId, @key, @requestHash, 'Processing', now())
                ON CONFLICT (customer_id, key) DO NOTHING
                """;
            AddParam(insertCommand, "customerId", customerId);
            AddParam(insertCommand, "key", key);
            AddParam(insertCommand, "requestHash", requestHash);

            var inserted = await insertCommand.ExecuteNonQueryAsync(ct);
            if (inserted == 1)
                return new IdempotencyClaim(IdempotencyOutcome.Claimed);
        }

        await using var selectCommand = connection.CreateCommand();
        selectCommand.CommandText =
            "SELECT request_hash, status, status_code, result_json FROM idempotency_keys WHERE customer_id = @customerId AND key = @key";
        AddParam(selectCommand, "customerId", customerId);
        AddParam(selectCommand, "key", key);

        await using var reader = await selectCommand.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new IdempotencyClaim(IdempotencyOutcome.StillProcessing);
        }

        var storedHash = reader.GetString(0);
        if (storedHash != requestHash)
            throw new IdempotencyKeyConflictException(key);

        var status = reader.GetString(1);
        if (status == "Processing")
            return new IdempotencyClaim(IdempotencyOutcome.StillProcessing);

        var statusCode = reader.GetInt32(2);
        var resultJson = reader.GetString(3);
        return new IdempotencyClaim(IdempotencyOutcome.ReplayCompleted, statusCode, resultJson);
    }

    public async Task CompleteAsync(string customerId, string key, int statusCode, string resultJson, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE idempotency_keys
             SET status = 'Completed', status_code = {statusCode}, result_json = {resultJson}
             WHERE customer_id = {customerId} AND key = {key}
             """, ct);
    }

    public async Task ReleaseAsync(string customerId, string key, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             DELETE FROM idempotency_keys
             WHERE customer_id = {customerId} AND key = {key} AND status = 'Processing'
             """, ct);
    }

    private static void AddParam(System.Data.Common.DbCommand command, string name, object value)
    {
        var param = command.CreateParameter();
        param.ParameterName = name;
        param.Value = value;
        command.Parameters.Add(param);
    }
}
