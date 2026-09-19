## Non-negotiable rules

- Money is `long`/`bigint` kobo everywhere. Never `float`/`double`/`decimal` in the money path.
- Transfers: lock both wallet rows via raw `SELECT ... FOR UPDATE`, ascending `wallet_id` order, one DB transaction covering validate+debit+credit+`transactions`+`audit_log`. Never split this into multiple transactions or drop the lock ordering.
- `audit_log` is append-only at the DB level (`REVOKE`/trigger). Never add `UPDATE`/`DELETE` against it — insert a correction instead.
- `Idempotency-Key` on `POST /transfers`: unique-constraint-based insert, replay-safe, payload-mismatch rejected.
- All errors are RFC 7807 Problem Details with a distinct HTTP status + `errorCode`.

## Project structure

`Domain` (entities, `Money` type, business rules — no EF/ASP.NET) → `Infrastructure` (EF Core, migrations, raw-SQL transfer, outbox) → `Api` (endpoints, middleware, DI). Plus `UnitTests` (Domain, no DB) and `IntegrationTests` (Testcontainers + Postgres). No separate Application/CQRS layer — don't add one without discussing it first.

## Conventions to preserve

- Transfer path uses raw SQL (no first-class `FOR UPDATE` in EF); everything else uses EF Core normally. Don't unify the style.
- Timestamps stored in UTC; WAT conversion happens only at daily-limit query time, no stored WAT value.
- Daily limit is computed fresh per request from `transactions`, no counter row, no reset job.
- `wallets` uniqueness is `(customer_id, currency, account_type)`, default `account_type = SAVINGS`. Don't collapse back to `UNIQUE(customer_id)`.
- Goal-based savings accounts are out of scope and are not rows in `wallets`.

## Testing & running

Every phase ships its own unit + integration tests as part of the same change. The concurrency test (parallel transfers, exact correct outcome, balance never negative) is the most important test in the repo — never weaken its assertions to fix flakiness.

`docker compose up` must bring up API+Postgres with no manual steps; Swagger must be reachable; `dotnet build` + both test projects must pass before a phase is considered done.
