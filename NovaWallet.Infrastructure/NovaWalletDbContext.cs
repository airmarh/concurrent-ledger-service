using Microsoft.EntityFrameworkCore;
using NovaWallet.Domain;

namespace NovaWallet.Infrastructure;

public class NovaWalletDbContext(DbContextOptions<NovaWalletDbContext> options) : DbContext(options)
{
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<OutboxEvent> Outbox => Set<OutboxEvent>();
    public DbSet<TransactionReversal> TransactionReversals => Set<TransactionReversal>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Wallet>(entity =>
        {
            entity.ToTable("wallets");
            entity.HasKey(w => w.WalletId);
            entity.Property(w => w.WalletId).HasColumnName("wallet_id");
            entity.Property(w => w.AccountNumber).HasColumnName("account_number").IsRequired().HasMaxLength(10).IsFixedLength();
            entity.Property(w => w.CustomerId).HasColumnName("customer_id").IsRequired();
            entity.Property(w => w.Currency).HasColumnName("currency").IsRequired().HasMaxLength(3);
            entity.Property(w => w.AccountType).HasColumnName("account_type").IsRequired().HasMaxLength(20);
            entity.Property(w => w.BalanceKobo).HasColumnName("balance_kobo").IsRequired();
            entity.Property(w => w.AllowsNegativeBalance).HasColumnName("allow_negative_balance").IsRequired();
            entity.Property(w => w.CreatedAt).HasColumnName("created_at").IsRequired();

            entity.HasIndex(w => new { w.CustomerId, w.Currency, w.AccountType })
                .IsUnique()
                .HasDatabaseName("ux_wallets_customer_currency_account_type");

            entity.HasIndex(w => w.AccountNumber)
                .IsUnique()
                .HasDatabaseName("ux_wallets_account_number");
        });

        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.ToTable("transactions");
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Id).HasColumnName("id");
            entity.Property(t => t.WalletId).HasColumnName("wallet_id").IsRequired();
            entity.Property(t => t.CounterpartyWalletId).HasColumnName("counterparty_wallet_id");
            entity.Property(t => t.AmountKobo).HasColumnName("amount_kobo").IsRequired();
            entity.Property(t => t.Direction).HasColumnName("direction").HasConversion<string>().HasMaxLength(10).IsRequired();
            entity.Property(t => t.CreatedAt).HasColumnName("created_at").IsRequired();

            entity.HasIndex(t => new { t.WalletId, t.CreatedAt })
                .HasDatabaseName("ix_transactions_wallet_id_created_at");
        });

        modelBuilder.Entity<AuditLogEntry>(entity =>
        {
            entity.ToTable("audit_log");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Id).HasColumnName("id");
            entity.Property(a => a.WalletId).HasColumnName("wallet_id").IsRequired();
            entity.Property(a => a.BalanceBeforeKobo).HasColumnName("balance_before_kobo").IsRequired();
            entity.Property(a => a.BalanceAfterKobo).HasColumnName("balance_after_kobo").IsRequired();
            entity.Property(a => a.Actor).HasColumnName("actor").IsRequired().HasMaxLength(50);
            entity.Property(a => a.CorrelationId).HasColumnName("correlation_id").IsRequired().HasMaxLength(100);
            entity.Property(a => a.CreatedAt).HasColumnName("created_at").IsRequired();

            entity.HasIndex(a => a.WalletId)
                .HasDatabaseName("ix_audit_log_wallet_id");
        });

        modelBuilder.Entity<OutboxEvent>(entity =>
        {
            entity.ToTable("outbox_events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.EventType).HasColumnName("event_type").IsRequired().HasMaxLength(50);
            entity.Property(e => e.PayloadJson).HasColumnName("payload_json").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.DispatchedAt).HasColumnName("dispatched_at");
            entity.Property(e => e.AttemptCount).HasColumnName("attempt_count").IsRequired();
            entity.Property(e => e.NextAttemptAt).HasColumnName("next_attempt_at");
            entity.Property(e => e.FailedAt).HasColumnName("failed_at");

            entity.HasIndex(e => e.DispatchedAt)
                .HasDatabaseName("ix_outbox_events_dispatched_at");
        });

        modelBuilder.Entity<TransactionReversal>(entity =>
        {
            entity.ToTable("transaction_reversals");
            entity.HasKey(r => r.OriginalTransactionId);
            entity.Property(r => r.OriginalTransactionId).HasColumnName("original_transaction_id");
            entity.Property(r => r.ReversalTransactionId).HasColumnName("reversal_transaction_id").IsRequired();
            entity.Property(r => r.Reason).HasColumnName("reason").IsRequired().HasMaxLength(200);
            entity.Property(r => r.CreatedAt).HasColumnName("created_at").IsRequired();
        });
    }
}
