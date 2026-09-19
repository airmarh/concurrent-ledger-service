namespace NovaWallet.Domain;

public abstract class DomainException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}

public class WalletNotFoundException : DomainException
{
    public WalletNotFoundException(Guid walletId)
        : base("WALLET_NOT_FOUND", $"Wallet '{walletId}' was not found.") { }

    public WalletNotFoundException(string accountNumber)
        : base("WALLET_NOT_FOUND", $"Wallet with account number '{accountNumber}' was not found.") { }
}

public class DuplicateWalletException(string customerId, string currency, string accountType)
    : DomainException("WALLET_ALREADY_EXISTS", $"Customer '{customerId}' already has a {accountType} wallet in {currency}.");

public class ReservedAccountTypeException(string accountType)
    : DomainException("RESERVED_ACCOUNT_TYPE", $"Account type '{accountType}' is reserved and cannot be used to create a wallet.");

public class ReservedWalletException(Guid walletId)
    : DomainException("RESERVED_WALLET", $"Wallet '{walletId}' is a system account and cannot be used in a transfer.");

public class InvalidAmountException(long amountKobo)
    : DomainException("INVALID_AMOUNT", $"Amount must be a positive number of kobo; received {amountKobo}.");

public class InsufficientFundsException(Guid walletId, long requestedKobo, long availableKobo)
    : DomainException("INSUFFICIENT_FUNDS", $"Wallet '{walletId}' has insufficient funds: requested {requestedKobo} kobo, available {availableKobo} kobo.");

public class SameWalletTransferException(Guid walletId)
    : DomainException("SAME_WALLET_TRANSFER", $"Cannot transfer from wallet '{walletId}' to itself.");

public class ForbiddenWalletAccessException(Guid walletId)
    : DomainException("FORBIDDEN_WALLET_ACCESS", $"You do not have access to wallet '{walletId}'.");

public class IdempotencyKeyConflictException(string key)
    : DomainException("IDEMPOTENCY_KEY_CONFLICT", $"Idempotency-Key '{key}' was already used with a different request payload.");

public class IdempotencyKeyProcessingException(string key)
    : DomainException("IDEMPOTENCY_KEY_PROCESSING", $"A request with Idempotency-Key '{key}' is already being processed. Retry shortly.");

public class DailyLimitExceededException(Guid walletId, long limitKobo, long alreadySpentTodayKobo, long requestedKobo)
    : DomainException(
        "DAILY_LIMIT_EXCEEDED",
        $"Wallet '{walletId}' would exceed its daily outbound limit of {limitKobo} kobo: already sent {alreadySpentTodayKobo} kobo today, requested {requestedKobo} kobo more.");

public class TransactionNotFoundException(Guid transactionId)
    : DomainException("TRANSACTION_NOT_FOUND", $"Transaction '{transactionId}' was not found.");

public class TransactionNotReversibleException(Guid transactionId)
    : DomainException("TRANSACTION_NOT_REVERSIBLE", $"Transaction '{transactionId}' is not an external-outbound debit and cannot be reversed.");

public class TransactionAlreadyReversedException(Guid transactionId)
    : DomainException("TRANSACTION_ALREADY_REVERSED", $"Transaction '{transactionId}' has already been reversed.");
