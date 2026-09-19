# Concurrent Ledger Service

**Repository:** https://github.com/airmarh/concurrent-ledger-service

A standalone C#/.NET 8 backend service implementing a wallet ledger for a digital wallet product. It owns wallet balances and every balance-mutating operation (create, credit, transfer) behind a locked, transactional, auditable core, guaranteeing under concurrent load that balances never go negative, transfers are atomic and idempotent, and every mutation is independently reconstructable from an immutable audit trail.

This is the shipped system after two rounds of design iteration on top of the original ledger definition — expanding to include a settlement account with true double-entry accounting, customer-facing account numbers, and external-outbound transfers with reversal, each addition argued on its own merits rather than treated as scope creep (see [AI usage](#ai-usage) below for how several of these were caught and corrected).

## Running it

```bash
docker compose up
```

This brings up Postgres and the API together, with no manual setup steps. EF Core migrations apply automatically on API startup.

- API: http://localhost:8080
- Swagger UI: http://localhost:8080/swagger

### Exercising the API

1. `POST /auth/token` with `{"customerId": "customer-1"}` to mint a JWT (see [Auth](#auth) below).
2. Paste the returned `accessToken` into Swagger's "Authorize" button (or send it as `Authorization: Bearer <token>`).
3. `POST /wallets` to create a wallet at zero balance — the response includes both `walletId` and a customer-facing `accountNumber`.
4. `POST /transactions/external-inbound` (with `accountNumber` in the body) to simulate a third-party (interbank/NIP) credit landing in it.
5. `POST /transactions/internal-transfers` (with an `Idempotency-Key` header) — `fromWalletId` is your own wallet's id, `toAccountNumber` is the recipient's account number.
6. `GET /wallets/{walletId}/transactions` to page through its transaction history.

## Running the tests

```bash
dotnet build
dotnet test NovaWallet.UnitTests
dotnet test NovaWallet.IntegrationTests
```

Unit tests exercise `Domain` logic only, with no database. Integration tests spin up a real Postgres instance via Testcontainers (requires Docker running locally) and exercise the full request → DB → response path, including the concurrency test described below.

## Architecture

```
NovaWallet.Domain          entities, the Money/kobo rules, business exceptions — no EF Core or ASP.NET references
NovaWallet.Infrastructure  EF Core DbContext, migrations, repositories, the raw-SQL locked-transfer code
NovaWallet.Api             minimal API endpoints, JWT middleware, Problem Details middleware, DI wiring
NovaWallet.UnitTests       tests Domain logic, no DB
NovaWallet.IntegrationTests  Testcontainers + real Postgres, tests Infrastructure/Api end-to-end
```

No separate Application/CQRS layer — five endpoints and one aggregate don't justify the ceremony.

### Endpoints

| Method | Route | Notes |
|---|---|---|
| `POST` | `/auth/token` | Dev-only mock JWT issuer, see [Auth](#auth) |
| `GET` | `/wallets` | Lists every wallet owned by the caller — the only way to recover a `walletId`/`accountNumber` after creation |
| `POST` | `/wallets` | Creates a wallet at zero balance for the caller |
| `GET` | `/wallets/{walletId}` | Balance + currency; caller must own the wallet |
| `GET` | `/wallets/by-account-number/{accountNumber}` | Same as above, looked up by account number; caller must own the wallet |
| `POST` | `/transactions/external-inbound` | Simulated third-party (interbank/NIP) credit; `accountNumber` + `amountKobo` in the body |
| `POST` | `/transactions/internal-transfers` | `FromWalletId` (own wallet) + `ToAccountNumber` (recipient) in the body; requires `Idempotency-Key` header; enforces the daily outbound limit and [rate limiting](#rate-limiting) |
| `POST` | `/transactions/external-outbound` | Debits the caller's own wallet to simulate an outbound third-party (interbank/NIP) transfer; `walletId` + `amountKobo` in the body; requires `Idempotency-Key` header and [rate limiting](#rate-limiting), same as `internal-transfers` |
| `POST` | `/transactions/external-outbound/{transactionId}/reversal` | Simulates the NIP switch reporting that an outbound transfer failed; `reason` in the body — see [Reversing a failed outbound transfer](#reversing-a-failed-outbound-transfer) |
| `GET` | `/wallets/{walletId}/transactions` | Paginated transaction history, newest first |
| `GET` | `/health/live` | Liveness probe; no auth, no rate limiting, see [Health/readiness](#healthreadiness) |
| `GET` | `/health/ready` | Readiness probe (checks Postgres); no auth, no rate limiting |

MVC controllers get Swagger grouping by controller class name automatically; Minimal APIs need it declared explicitly. Every endpoint group here calls `.WithTags(...)` for exactly that reason — Swagger UI shows three collapsible sections (Auth, Wallets, Transactions) instead of one flat list. `internal-transfers` (intrabank, both wallets held by NovaWallet), `external-inbound` (interbank, a simulated third-party/NIP credit), and `external-outbound` (interbank, a simulated third-party/NIP debit) are deliberately separate endpoints rather than one generic "create transaction" endpoint with a direction flag — they differ in caller identity (a customer-initiated transfer vs. a system/webhook standing in for another bank's message), ownership checks, idempotency, and rate limiting, so collapsing them would trade structural guarantees for conditional logic. See [AI_USAGE.md](AI_USAGE.md) for the fuller reasoning.

### Schema

- `wallets(wallet_id PK, account_number, customer_id, currency, account_type, balance_kobo, allow_negative_balance, created_at)` — `UNIQUE(customer_id, currency, account_type)` and a separate `UNIQUE(account_number)`. `account_type` defaults to `SAVINGS`. The unique key is on the triple, not just `customer_id`, so a customer can later hold more than one singleton account type (e.g. `CURRENT`) under the same currency without a schema change. `allow_negative_balance` is `true` for exactly two rows — the fixed inbound and outbound settlement accounts (see [Settlement accounts](#settlement-accounts-for-simulated-credits-and-debits) below) — and `false` for every customer wallet.
- `transactions(id PK, wallet_id FK, counterparty_wallet_id, amount_kobo, direction, created_at)` — the customer-facing transaction-history record, indexed on `(wallet_id, created_at)`.
- `audit_log(id PK, wallet_id FK, balance_before_kobo, balance_after_kobo, actor, correlation_id, created_at)` — see [Why two tables?](#why-two-tables-transactions-vs-audit_log) below.
- `idempotency_keys(customer_id, key, request_hash, status, status_code, result_json, created_at)` — composite `PRIMARY KEY (customer_id, key)`, not a single global `key`, so two different customers can never collide on the same key string.
- `outbox_events(id PK, event_type, payload_json, created_at, dispatched_at, attempt_count, next_attempt_at, failed_at)` — see [Outbox pattern](#outbox-pattern-for-money-movement-events) below.
- `transaction_reversals(original_transaction_id PK, reversal_transaction_id, reason, created_at)` — see [Reversing a failed outbound transfer](#reversing-a-failed-outbound-transfer) below. The one table here whose PK isn't a freshly generated id — it's the `id` of the `transactions` row being reversed, so the primary key itself is what prevents reversing the same transaction twice.

All other PKs are generated as UUIDv7 (`NovaWallet.Domain.UuidV7`), not `Guid.NewGuid()`'s fully-random UUIDv4 — a time-ordered high bit range means inserts land near the tail of each PK's B-tree index instead of at a random position, avoiding the index fragmentation a v4 GUID PK causes at large row counts, while keeping GUID's client-side generation (no DB round-trip needed before an ID exists) and non-enumerable, coordination-free properties that a switch to `bigint` would have given up.

### Account number

`wallet_id` (a GUID) stays the permanent internal identifier every FK (`transactions`, `audit_log`, outbox payloads) actually references — it's never customer-facing. `account_number` is a separate, `NovaWallet.Domain.AccountNumberGenerator`-issued random 10-digit string assigned at `Wallet.Create()` time, unique via a DB constraint, that a customer can actually type or read aloud. `GET /wallets/by-account-number/{accountNumber}` resolves one to the wallet it identifies (with the same ownership check as `GET /wallets/{walletId}`). On the rare event a freshly generated number collides with an existing one (10^10 possible values — astronomically unlikely, but the DB unique constraint is the actual guarantee, not the odds), `WalletRepository.AddAsync` retries the insert with a newly generated number rather than failing the request.

Both money-moving endpoints are addressed by account number where a human is the one supplying the identifier, and by `wallet_id` where the caller's own client already has it:

- `POST /transactions/internal-transfers` takes `FromWalletId` (`Guid` — the sender's own wallet, picked from a list their client already has) and `ToAccountNumber` (`string` — the recipient, the identifier a person actually types). The API layer resolves `ToAccountNumber` to a `wallet_id` via `IWalletRepository.GetByAccountNumberAsync` before calling `TransferService` — none of the locking/double-entry logic changed, only what identifies the destination at the boundary.
- `POST /transactions/external-inbound` takes `AccountNumber` in the body rather than a route segment, since a real inbound NIP credit is addressed by the recipient's account number — an internal ledger ID isn't something an external sending bank would ever have.

### Settlement accounts, for simulated credits and debits

`POST /transactions/external-inbound` doesn't just increase a wallet's balance — it writes a proper double-entry pair, the same as a transfer: the customer's wallet is credited, and a fixed, non-customer-facing **inbound** settlement account (`NovaWallet.Domain.WellKnownWalletIds.NgnInboundSettlementAccount`, seeded once by migration, never created via `POST /wallets`) is debited by the same amount, under the same ascending-`wallet_id` locking discipline as `TransferService` (factored into a shared `WalletLock` helper). `POST /transactions/external-outbound` is its mirror image (`OutboundService.DebitAsync`): the customer's wallet is debited and a separate, fixed **outbound** settlement account (`NgnOutboundSettlementAccount`) is credited by the same amount, same locking discipline.

Inbound and outbound settlement are deliberately two different wallets rather than one shared account netting both directions together: they represent different exposures to the (simulated) external NIP network — inbound is the risk that a credit NovaWallet has already paid out to a customer never actually clears; outbound is the risk that a debit NovaWallet already took from a customer never actually leaves. Netting them into one balance would hide which direction is driving any given exposure and make reconciling against the real settlement network harder than it needs to be. Both are `wallets` rows under the same reserved `customer_id` (`SYSTEM_SETTLEMENT`) but different `account_type` (`SETTLEMENT_INBOUND` / `SETTLEMENT_OUTBOUND`) — the existing `(customer_id, currency, account_type)` uniqueness rule already supports one customer holding more than one wallet, so no schema change beyond seeding the second row was needed. Both are the only rows with `allow_negative_balance = true`, since each represents a liability to (or a claim against) the outside (simulated) NIP network rather than actual funds — every customer wallet is still guaranteed to never go negative.

Both are also explicitly walled off from the general transfer path: `TransferService.TransferAsync` rejects either as `fromWalletId` or `toWalletId` before taking any lock, and `POST /auth/token` refuses to mint a token for their shared reserved `customerId`. Without both, their negative-balance allowance would be an unlimited money-creation path through `POST /transactions/internal-transfers` rather than something only `WalletCreditService`/`OutboundService`'s double-entry operations can touch — see `AI_USAGE.md` for how this was found, and for the later suggestion to split the single account into these two.

### Reversing a failed outbound transfer

An outbound NIP debit isn't atomic the way an intrabank transfer is: the internal ledger commits immediately (the customer's wallet really is debited, right away, same as everywhere else in this system), but whether the receiving bank actually accepts the funds is something NovaWallet only finds out afterward. `POST /transactions/external-outbound/{transactionId}/reversal` simulates that after-the-fact report — the NIP switch telling NovaWallet a specific outbound transfer failed — and reverses it: a brand-new debit/credit pair crediting the customer back and debiting the outbound settlement account back out (`OutboundService.ReverseAsync`), never touching the original rows. This fits the append-only ledger model already in place for `audit_log`: a correction is always a new insert, never an update or delete of history.

The one new piece of schema this needed is `transaction_reversals(original_transaction_id PK, reversal_transaction_id, reason, created_at)` — a small, dedicated table rather than a nullable column bolted onto `transactions` (the busiest table in the system, growing with every debit/credit ever made). `original_transaction_id` being the *primary key*, not just a unique index, does double duty: it's the row's natural identity, and it's also the actual guarantee that the same original transaction can never be reversed twice — a second concurrent reversal attempt hits a PK violation (`23505`) and is translated to `409 TRANSACTION_ALREADY_REVERSED`, never silently crediting the customer back twice. `ReverseAsync` also checks that the transaction being reversed is actually a Debit against the settlement account (i.e. really came from `external-outbound`) before doing anything, rejecting anything else — an ordinary transfer's debit leg, say — with `422 TRANSACTION_NOT_REVERSIBLE`.

### Money

Every amount, everywhere in the money path (`Domain` and `Infrastructure`), is a `long`/`bigint` count of kobo. No `float`/`double`/`decimal` appears anywhere near balance arithmetic.

### Concurrency & atomicity

Transfers lock both wallet rows with raw `SELECT ... FOR UPDATE`, always in ascending `wallet_id` order (so two transfers moving money in opposite directions between the same pair of wallets can never deadlock), inside a single database transaction that covers validation, debit, credit, both `transactions` inserts, and both `audit_log` inserts. Any failure rolls back the entire operation — no partial state is ever observable.

The most important test in the repo (`NovaWallet.IntegrationTests/InternalTransfersEndpointsTests.cs`) fires many parallel transfer requests via `Task.WhenAll` against a wallet with only enough balance for a subset to succeed, and asserts the exact correct number succeed, the balance is exactly correct afterward, and it is never observed negative at any point. This assertion is intentionally never weakened to chase flakiness — see [`AI_USAGE.md`](AI_USAGE.md) for a real concurrency bug this test caught that per-request status codes alone would have missed entirely.

### Idempotency

`POST /transactions/internal-transfers` and `POST /transactions/external-outbound` both require an `Idempotency-Key` header — both debit a customer's own wallet on the caller's initiative, so both get the same replay-safety guarantee. The server attempts a unique-constraint-based insert of `(customer_id, key, request_hash, status=pending)`; a conflict with a matching hash replays the stored result without reprocessing, a conflict with a differing hash is rejected with `409 IDEMPOTENCY_KEY_CONFLICT`, and concurrent replays of the same key still result in exactly one processed request. `external-inbound` and the outbound reversal endpoint don't require one — neither is initiated by a customer's own client liable to retry a dropped connection; the reversal in particular is already naturally one-time-only, guarded by `transaction_reversals`' primary key rather than a client-supplied key.

**Known limitation: `idempotency_keys` has no retention policy.** `IdempotencyStore.CompleteAsync` writes a row that's never deleted afterward (`ReleaseAsync` only removes a row stuck in `Processing`, i.e. a genuinely failed attempt) — every successfully completed transfer or external-outbound request leaves a permanent row, forever. Unlike `transactions`/`audit_log`/`transaction_reversals`, which must be kept forever as the actual ledger and audit trail, this table's only purpose is protecting against a *near-term* retry of the same request — once that window has passed, a row has no future value to anyone (a client isn't going to replay a months-old request under the same key), so unlike the ledger tables this one is safe, and correct, to prune. The right fix is a bounded TTL (e.g. 24–72 hours from `created_at`) deleted by a scheduled job, the same shape as the existing `OutboxDispatcherHostedService` (a `BackgroundService` on a `PeriodicTimer`) — not archiving, since there's no future read value to preserve. This would also need an index on `created_at`, which the table doesn't currently have (only the `(customer_id, key)` primary key). Not implemented — recorded here as a known gap rather than a silent one; see `AI_USAGE.md`.

### Auth

All business endpoints require a JWT bearer token, validated by ASP.NET's real `JwtBearer` middleware (issuer, audience, signature, expiry all checked). `POST /auth/token` is a dev-only mock issuer that mints an HMAC-signed JWT carrying a `customer_id` claim for a given `customerId` — clearly labeled as such in Swagger, and not a substitute for a real OAuth2/OIDC provider in production. `customer_id` is always taken from the validated token, never from a request body, so a caller can never act on another customer's wallet. See [A gateway in front of this ledger](#a-gateway-in-front-of-this-ledger) for what this token would more realistically represent in a real deployment.

### Errors

Every error response is an RFC 7807 Problem Details body with a distinct HTTP status and a stable, machine-readable `errorCode` extension field, so client code can branch on failure type without parsing free text (e.g. `404 WALLET_NOT_FOUND`, `422 INSUFFICIENT_FUNDS`, `409 IDEMPOTENCY_KEY_CONFLICT`, `403 FORBIDDEN_WALLET_ACCESS`).

### Transaction history pagination

`GET /wallets/{walletId}/transactions` uses classic `page`/`pageSize` query parameters (`page` defaults to `1`, `pageSize` defaults to `20` and is capped at `100`) rather than a cursor. A cursor is the better choice for a feed that changes underneath the reader while they scroll, but a customer's own transaction history is read far more than it's concurrently written to by that same customer, and page/pageSize is simpler to implement, test, and consume from a mobile client (jump to page 3, show a page count) at no real cost here. The response includes `page`, `pageSize`, `totalCount`, and `totalPages` alongside the items. This is transaction history, not a bank statement (a distinct future feature — a static, dated document) — the naming was deliberately corrected to avoid conflating the two.

### Why the EF Core / raw-SQL split?

Most of the app — wallet creation, balance reads, the transaction-history query, migrations — uses EF Core normally. The transfer path specifically drops to raw parameterized SQL inside an explicit ADO.NET transaction, because EF Core has no first-class `SELECT ... FOR UPDATE` support, and the transfer path is the one place where the exact lock semantics (which rows, in which order, held for exactly how long) must be guaranteed rather than inferred through an ORM abstraction. This is a deliberate two-data-access-styles decision, not inconsistency — see [`AI_USAGE.md`](AI_USAGE.md) for a real bug this distinction exists to prevent (routing a locking read through EF's tracked-entity pipeline silently broke lock semantics under concurrent load).

### Why two tables? `transactions` vs `audit_log`

`transactions` is the customer-facing transaction-history record — one row per leg of a movement, with a counterparty wallet reference. `audit_log` is a materially different table: it records `balance_before`/`balance_after`, an actor, and a correlation id for every balance-mutating event, so a wallet's entire balance history can be independently reconstructed by replaying its audit rows — the mechanism for catching a ledger bug if the two tables were ever found to disagree. It's also immutable at the database level (`REVOKE UPDATE, DELETE`, not merely an app-layer convention), verified by an integration test that attempts a raw `UPDATE` against Postgres directly and asserts it is rejected. The two tables looking similar is intentional, not duplication: one is for customers, the other is for regulators and internal reconciliation.

### Structured logging & correlation IDs

Every request gets a correlation ID, established by `CorrelationIdMiddleware` before anything else in the pipeline runs: an incoming `X-Correlation-Id` header is honored verbatim, otherwise a new one is generated. It's echoed back on every response (even error responses), and pushed into Serilog's `LogContext` for the lifetime of the request, so every log line the request produces — EF Core's SQL commands, the request-completed summary line, anything an endpoint logs — carries that same `CorrelationId` field in the structured JSON console output. Mutating endpoints (`external-inbound`, `internal-transfers`) read it via `HttpContext.GetCorrelationId()` and write it into the `audit_log` row(s) they create, so a single ID ties together "what a client sent," "what got logged," and "what got persisted" for one request. See `NovaWallet.Api/Middleware/CorrelationIdMiddleware.cs`.

### Health/readiness

`GET /health/live` and `GET /health/ready` are suitable for a container orchestrator's liveness/readiness probes, and are excluded from both JWT auth and the transfers rate limiter (`.AllowAnonymous()` + `.DisableRateLimiting()`). Liveness (`Predicate = _ => false`) runs zero checks and just confirms the process is up — it must never depend on a downstream dependency, or a Postgres blip would get a healthy pod killed by the orchestrator for no reason. Readiness runs `PostgresHealthCheck` (a real `db.Database.CanConnectAsync()`), returning `200` when Postgres is reachable and `503` otherwise, so a load balancer stops routing traffic to an instance that can't actually serve requests.

### Rate limiting

`POST /transactions/internal-transfers` and `POST /transactions/external-outbound` are fronted by ASP.NET Core's built-in fixed-window rate limiter (`Microsoft.AspNetCore.RateLimiting`, part of the .NET 8 shared framework — no extra package), scoped to those two endpoints via `.RequireRateLimiting("transfers")` so every other route is unaffected. Requests are partitioned per authenticated `customer_id` (falling back to remote IP if that claim is ever missing), so one customer hammering the endpoint can't burn through another customer's budget. The limit is `30` requests per `60`-second window by default, configurable via `RateLimit:PermitLimit`/`RateLimit:WindowSeconds`; `QueueLimit` is `0`, so a request over the limit is rejected immediately with `429 RATE_LIMIT_EXCEEDED` rather than being queued and delayed — for a money-movement endpoint, an immediate, honest rejection beats a slow, silent one. The rejection is written through the same `ProblemDetailsWriter` the unhandled-exception handler uses, so it comes back in the identical RFC 7807 shape as every other error.

### Daily outbound limit

Every `internal-transfers` transfer and `external-outbound` debit is checked, inside the same locked transaction as the debit/credit, against a per-wallet daily outbound limit (`₦500,000` / `50,000,000` kobo by default, configurable via `TransferLimits:DailyOutboundLimitKobo`). Both endpoints run the identical query — the sum of that wallet's outbound `transactions` since the start of the current WAT (fixed UTC+1, no DST) calendar day, regardless of counterparty — and reject with `429 DAILY_LIMIT_EXCEEDED` if adding the new amount would exceed it, mutating nothing if it does. That shared query is what makes the limit a genuine per-wallet daily cap rather than two independent ones: spending part of it via a transfer leaves only the remaining headroom available through an outbound debit, and vice versa (`DailyLimit_IsSharedBetweenInternalTransferAndExternalOutboundDebit` in `DailyLimitTests.cs`).

There is no stored counter, no reset job, and no stored WAT value: timestamps stay in UTC everywhere, and "today" in WAT is derived fresh from the current UTC instant at query time (`NovaWallet.Domain.WestAfricaTime`). Because the check runs after each endpoint's existing `SELECT ... FOR UPDATE` lock on the source wallet is already held, a concurrent burst out of the same wallet is naturally serialized by that lock — each waits for the previous one to commit before it can sum "today's spend," so the limit holds exactly under concurrency, not just per-request. The "now" used for the boundary comes from an injected `IClock` (`SystemClock` in production) so tests can pin it precisely without waiting for real midnight WAT.

### Outbox pattern for money-movement events

Every successful money-movement operation writes one row to `outbox_events` inside the exact same database transaction as its debit/credit — `TransferCompleted` (`TransferService.TransferAsync`), `ExternalInboundCreditCompleted` (`WalletCreditService.CreditAsync`), `ExternalOutboundDebitCompleted` (`OutboundService.DebitAsync`), and `ExternalOutboundReversed` (`OutboundService.ReverseAsync`) — each with its own JSON payload shape (transaction id(s), wallet id, amount, correlation id, plus `reason` for a reversal). This is what makes the event durable even if the process crashes the instant after that commit: the row is already safely in Postgres regardless of whether anything ever gets a chance to publish it in that moment. All four exist for the same reason: a downstream consumer (e.g. notifications) reacting to "money moved" shouldn't only find out about intrabank transfers and miss every other way a wallet's balance can change.

A singleton `OutboxDispatcherHostedService` (a `BackgroundService`) polls every `Outbox:PollIntervalSeconds` (`5`s by default) for rows where `dispatched_at IS NULL`, "publishes" each as a structured log line via `ILogger` (one line shape per event type, dispatched by `EventType` in `OutboxDispatcher.Publish`) — with the original correlation ID as a named property, so it's traceable back to the request that caused it — and then sets `dispatched_at`, which is what stops a later poll from re-publishing the same row. No message broker is added to the stack; the outbox's durability guarantee, not the transport, is what's being demonstrated. `IOutboxDispatcher.DispatchPendingAsync()` is also directly callable outside the timer loop, which is how the integration tests simulate "the next poll" deterministically instead of sleeping for real wall-clock seconds.

**Per-event retry with backoff.** Each event in a poll batch is dispatched independently — one event throwing (a malformed payload, say) no longer aborts the batch's `SaveChangesAsync` for every other row in it, which is what used to let one broken event permanently block every legitimate event queued behind it (see `AI_USAGE.md`). A failed attempt increments `attempt_count` and sets `next_attempt_at` with exponential backoff (`OutboxRetry:BaseDelaySeconds`, doubling per attempt, capped at `OutboxRetry:MaxDelaySeconds`; `5s`/`300s` by default) — the poll query excludes any row whose `next_attempt_at` is still in the future, so a broken event backs off instead of being retried every single tick. After `OutboxRetry:MaxAttempts` (`10` by default) the event is marked `failed_at` and stops being retried automatically.

**Safe with more than one dispatcher instance running at once.** `docker-compose.yml` only ever runs one `api` service, but `OutboxDispatcherHostedService` is written as if it might not be alone: `OutboxDispatcher.DispatchPendingAsync` reads its batch with a raw `SELECT ... FOR UPDATE SKIP LOCKED` (the same locked-read discipline `WalletLock` uses for wallet rows, applied here to `outbox_events`) inside an explicit transaction that isn't committed until every row's outcome is written. A second poller — another pod, another replica — racing the same poll window locks a disjoint set of rows instead of racing the first one to publish the same event twice, and a poller that crashes mid-batch simply rolls its transaction back, releasing the locks so the next poll (from any instance) picks the rows back up, with no separate lease/expiry bookkeeping required. See `AI_USAGE.md` for how this gap was found before it was ever a real, live bug.

**Known limitation:** there is no dead-letter queue, alerting, or manual-replay tooling for events that reach `failed_at` — they're durably flagged and queryable, but nothing currently surfaces that to an operator beyond the `LogError` line at the moment they exhaust retries. Acceptable for this project's scope; a real deployment would want a dashboard/alert on `WHERE failed_at IS NOT NULL`.

## Build status

All 10 phases of the original plan are implemented and tested: foundations/auth/wallet creation, credit + audit trail, atomic concurrency-safe transfers, idempotent transfers, the paginated transaction history, the WAT-based daily outbound limit, structured logging with correlation IDs, rate limiting on transfers, liveness/readiness health checks, and the outbox pattern for `TransferCompleted`. Five more phases followed real development past that plan: the settlement account and true double-entry credits, customer-facing account numbers and wallet listing, ledger hardening (UUIDv7 PKs, outbox retry/backoff), the endpoint restructuring under `/transactions`, and external-outbound transfers with reversal.

## Out of scope

Notably: additional `account_type` values beyond `SAVINGS`, goal-based savings accounts (a different, non-`wallets` data shape), a real OAuth2/OIDC provider, a precomputed transaction-history read-model, a caching layer for the daily-limit check, a real message broker for the outbox pattern, an actual bank statement (a distinct, dated document — not what `GET /wallets/{walletId}/transactions` is), and a real, built gateway/orchestration layer in front of this ledger (see below).

### A gateway in front of this ledger

In a real deployment, a client app would never call this service directly — a gateway/orchestration layer would sit in front of it and own everything that isn't posting money. Concretely, that layer would: terminate the customer's actual authentication (the real OAuth2/OIDC provider called out above, not this repo's dev-only `POST /auth/token`); decide what kind of movement a request represents (a peer transfer vs. a third-party one, which rail to route it over) and run whatever business-level validation that decision needs (fraud/velocity checks, compliance screening, KYC status); and only then call into this ledger's narrow, specific posting operations with an already-classified, already-authorized instruction. This ledger's own job stays limited to what it's actually built to guarantee — atomic posting, correct locking, and an accurate audit trail — not classifying transfer types or authenticating end users.

This is also the deeper reason `internal-transfers`, `external-inbound`, and `external-outbound` stay three separate, narrow endpoints rather than one generic "create transaction" endpoint with a type flag: that generality — accepting one client-facing request and deciding how to route it — is the gateway's responsibility, not the ledger's. Folding it into this service would blur the one boundary that matters most here: *deciding* what a movement is belongs one layer up; *posting* it correctly and atomically belongs here. In this setup, the JWT this service validates directly would more realistically be a narrowly scoped internal service token issued by that gateway (or mTLS between services), not the same credential sitting on the customer's device — this service would sit on a private network path reachable only from the gateway, never exposed to the public internet or a client app directly.

## AI usage

This project was built with heavy use of Claude via Claude Code, including for the initial design interview, the phased plan, and writing/debugging the implementation. See [`AI_USAGE.md`](AI_USAGE.md) for representative prompts and specific issues caught during development — a genuine concurrency/double-spend bug the transfer-locking code shipped with initially, a wallet-cardinality design choice the AI got wrong until steered toward the schema's actual future requirement, and a self-inflicted false pass in the daily-limit boundary test from mixing a fake clock with real timestamps, among others.
