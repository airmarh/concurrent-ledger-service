using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NovaWallet.Domain;

namespace NovaWallet.Infrastructure;

internal static class WalletLock
{
    public static async Task<Wallet?> LockAsync(NovaWalletDbContext db, Guid walletId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT account_number, customer_id, currency, account_type, balance_kobo, allow_negative_balance, created_at FROM wallets WHERE wallet_id = @walletId FOR UPDATE";
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        var param = command.CreateParameter();
        param.ParameterName = "walletId";
        param.Value = walletId;
        command.Parameters.Add(param);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return Wallet.FromPersisted(
            walletId,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetBoolean(5),
            reader.GetDateTime(6));
    }
}
