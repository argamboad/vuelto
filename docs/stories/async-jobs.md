# Stories — Async Work: Outbox, Inbox & Scheduled Jobs

> One file per epic. Makes side effects reliable: a **transactional outbox** (effects committed
> atomically with the data change), a background **dispatcher**, an **inbox** for idempotent inbound
> deliveries (e.g. Stripe webhooks), and a host for **scheduled/recurring** work. **Status: ✅ COMPLETE**
> — JOBS-1 (outbox + dispatcher + email migration), JOBS-2 (inbox dedup gate), and JOBS-3 (scheduled-
> jobs host + token-cleanup job) all shipped. Design decision and constraints in **ADR-007** (see its
> 2026-06-25 amendment). Stories use Gherkin acceptance criteria. This epic was the prerequisite for
> reliable billing webhooks (`docs/stories/billing.md`) — **BILLING is now unblocked.**

**Epic key:** `JOBS`

**Prerequisites (external, before any code):**
- None beyond the existing stack — the platform's design keeps this **in-process on Postgres**, no
  Redis/broker (ADR-C13: run cost stays "Postgres only"). A distributed scheduler (Hangfire/Quartz)
  is a documented swap-in, not a dependency now.

**Why this exists:** the platform currently sends email **inline in the request thread** —
passwordless send and invitations call `IEmailSender.SendAsync` mid-request
([`SmtpEmailSender`](../../src/Infrastructure/Email/SmtpEmailSender.cs)). A transient SMTP failure
surfaces as a request error or a silently lost email, and "saved the row but the email never went"
is not atomic. The outbox fixes this generically so every future side effect inherits reliability.

---

### JOBS-1 — Transactional outbox + dispatcher (email is the first consumer)

**Status: ✅ Implemented** (`feat/jobs-1-outbox-dispatcher`). Entity `src/Core/Entities/OutboxMessage.cs`;
abstractions `src/Core/Abstractions/IOutbox.cs` + `IOutboxHandler.cs`; infra `src/Infrastructure/Outbox/`
(`EfOutbox`, `OutboxProcessor`, `OutboxDispatcher`) + `src/Infrastructure/Email/`
(`OutboxEmailSender` decorator, `EmailOutboxHandler`); migration `AddOutbox`; tests
`tests/Api.Tests/Outbox/`. All three email call sites (passwordless ×2, invitations) were migrated
with zero changes — they still depend on `IEmailSender`, which is now the outbox decorator.

**As a** the platform
**I want** side effects enqueued in the same transaction as the data change and dispatched in the
background
**So that** an effect is never lost and never half-applied

**Context / notes:** an `OutboxMessage` row is written through
[`IUnitOfWork`](../../src/Infrastructure/Repositories/EfUnitOfWork.cs) in the **same `SaveChanges`**
as the business change — atomic by construction. A `BackgroundService` (`OutboxDispatcher`) polls
unsent rows, invokes a typed handler, and marks sent; failures retry with backoff and land in a
**dead-letter** state after N attempts. `OutboxMessage` is **not** `ITenantScoped` (it's platform
infra and may carry system effects) but stores an optional `TenantId` for handler context. This
slice **migrates the existing email sends** (passwordless, invitations) to enqueue → handler, on
**existing behavior** — the safest way to prove the path (Mailpit still receives the mail).

> **2026-07-15 — commit-time fault bookkeeping (v3 audit LB-BILL-2).** `OutboxProcessor` staged the handler
> result + `Status=Sent` inside a `try` but ran `SaveChanges`/`Commit` **outside** it. A commit-time fault
> (transient disconnect, or a handler that stages a constraint-violating row that only faults at
> `SaveChanges`) rolled the whole thing back, so `AttemptCount` never advanced — the message stayed
> `Pending`, re-ran the side effect every pass, and **never dead-lettered** (a poison-at-commit loop). Now
> the save+commit are inside the `try`, and any failure records the attempt in a **separate transaction**
> (`RecordFailedAttemptAsync`, re-claiming the row `FOR UPDATE`) so bookkeeping survives the rollback and a
> poison message eventually dead-letters. Test: `ProcessDue_HandlerStagesARowThatFaultsAtCommit_StillAdvancesAttempt_AndDeadLetters`.

**Acceptance criteria**

```gherkin
Scenario: An effect and its data change commit together
  Given a request that creates a row and enqueues an email
  When the database transaction commits
  Then exactly one OutboxMessage row exists alongside the data change
  And if the transaction rolls back, neither the row nor the message exists

Scenario: The dispatcher delivers and marks sent
  Given an unsent OutboxMessage for an email
  When the dispatcher runs
  Then the email is delivered (Mailpit receives it) and the message is marked sent

Scenario: A failing handler retries then dead-letters
  Given a handler that fails N times
  When the dispatcher retries up to the limit
  Then the message is marked dead-lettered (not retried forever) and is observable

Scenario: Passwordless and invitation email now go through the outbox
  Given a magic-link / OTP / invitation request
  When it is handled
  Then the email is enqueued (not sent inline) and delivered by the dispatcher
  And the request no longer fails if SMTP is momentarily down
```

**Out of scope:** inbound dedupe (JOBS-2); scheduling (JOBS-3); a distributed broker.
**Definition of done:** tests first; atomicity (rollback leaves no message), dispatch, retry, and
dead-letter unit/integration-tested against the Postgres Testcontainer; the email migration covered
by the existing auth/invitation tests still passing; merged, app working; ADR-007 referenced.

---

### JOBS-2 — Inbox: idempotent inbound delivery (webhook dedupe)

**Status: ✅ Implemented** (`feat/jobs-2-inbox`). Entity `src/Core/Entities/InboxMessage.cs`;
abstraction `src/Core/Abstractions/IInbox.cs`; impl `src/Infrastructure/Inbox/EfInbox.cs`
(`INSERT … ON CONFLICT DO NOTHING` on a unique `(Source, IdempotencyKey)` index — race-free, no
`SKIP LOCKED`); migration `AddInbox`; tests `tests/Api.Tests/Inbox/`. See the ADR-007 amendment for
why it's a separate ledger rather than a `direction` column. BILLING-3 consumes it.

**As a** the platform
**I want** at-least-once inbound events processed exactly once
**So that** retried/duplicated/out-of-order webhooks don't double-apply

**Context / notes:** the inbox is the outbox's mirror — same table shape with a `direction`
discriminator, keyed by an external **idempotency id** (e.g. the Stripe event id). A handler claims
a row, processes it once, and records completion; redelivery of a seen id is a no-op ack. This is
what `BILLING-3` consumes. Uses Postgres `FOR UPDATE SKIP LOCKED` claim semantics so it's safe if
the dispatcher ever runs multi-instance.

**Acceptance criteria**

```gherkin
Scenario: First delivery is processed
  Given an inbound event with a new idempotency id
  When it is received
  Then it is recorded and its handler runs exactly once

Scenario: Duplicate delivery is a no-op
  Given an idempotency id already recorded as processed
  When the same event is delivered again
  Then it is acknowledged with no re-processing

Scenario: Concurrent duplicate deliveries process once
  Given two deliveries of the same id arrive concurrently
  When both are claimed
  Then only one handler runs (the other observes it already claimed/processed)
```

**Out of scope:** Stripe signature verification (lives at the BILLING webhook endpoint);
provider-specific payload parsing.
**Definition of done:** tests first; first/duplicate/concurrent paths integration-tested on the
Postgres Testcontainer (`SKIP LOCKED` proven); merged, app working.

---

### JOBS-3 — Scheduled / recurring jobs host

**Status: ✅ Implemented** (`feat/jobs-3-scheduler`). Abstraction `src/Core/Abstractions/IScheduledJob.cs`;
host `src/Infrastructure/Scheduling/ScheduledJobsHost.cs` (timer `BackgroundService`; per-job
intervals; failure isolation; fresh DI scope per run); reference job
`src/Infrastructure/Scheduling/ExpiredTokenCleanupJob.cs`; tests `tests/Api.Tests/Scheduling/`. Add a
job by registering an `IScheduledJob` — no host edits. Future jobs (trial-expiry sweeps, quota resets)
copy the cleanup job's shape.

**As a** the platform
**I want** a place to run periodic background work
**So that** sweeps and nudges (trial expiry, dunning, token cleanup, quota resets) happen on time

**Context / notes:** a lightweight timer `BackgroundService` (`ScheduledJobsHost`) runs registered
`IScheduledJob`s on intervals/cron. In-process for the platform; document the swap to Hangfire/Quartz
when multi-node arrives (ADR-007). First real jobs are owned by other epics (BILLING trial-expiry,
expired-`LoginToken`/`RefreshToken` cleanup) — this slice ships the **host + one reference job**.

**Acceptance criteria**

```gherkin
Scenario: A registered job runs on its schedule
  Given a job registered to run on an interval
  When the host is running and the interval elapses
  Then the job executes
  And a failure in one job does not stop the host or other jobs

Scenario: Reference cleanup job purges expired tokens
  Given expired LoginToken / RefreshToken rows exist
  When the cleanup job runs
  Then those rows are deleted and active ones are untouched
```

**Out of scope:** distributed/leader-elected scheduling; per-tenant cron.
**Definition of done:** tests first; schedule firing + failure-isolation + the cleanup job's
boundaries unit/integration-tested; merged, app working.

---

## Slice plan (implementation map — when undeferred)

Ordered, each a mergeable vertical slice. TDD throughout.

1. ✅ **Outbox + dispatcher + email migration (JOBS-1).** — DONE.
   - `Core/Entities/OutboxMessage.cs` (id, type, payload jsonb, optional `TenantId`, status,
     attempt_count, next_attempt_at, created_at) + EF config + migration. **Not** `ITenantScoped`.
   - `IOutbox.Enqueue(message)` writing through the **same** `IUnitOfWork` as the caller's change.
   - `OutboxDispatcher : BackgroundService` — poll unsent (claim with `FOR UPDATE SKIP LOCKED`),
     resolve a typed `IOutboxHandler`, deliver, mark sent / retry / dead-letter.
   - Migrate `PasswordlessService` + invitation sends from inline `IEmailSender` to enqueue +
     `EmailOutboxHandler` (the SMTP send moves into the handler).
   - **Tests first:** rollback-leaves-no-message; dispatch; retry→dead-letter; existing auth/invite
     email tests still green.
2. ✅ **Inbox (JOBS-2).** — DONE. Separate `InboxMessage` ledger; `IInbox.TryClaimAsync(source, key)`
     via `INSERT … ON CONFLICT DO NOTHING` on a unique index (race-free); concurrency test included.
     Consumed by BILLING-3. (Built as a dedup ledger, not a `direction` column — ADR-007 amendment.)
3. ✅ **Scheduled host (JOBS-3).** — DONE. `ScheduledJobsHost : BackgroundService` + `IScheduledJob`
     (per-job intervals, failure isolation, fresh scope per run); reference `ExpiredTokenCleanupJob`.

**Dissolve interaction (note):** pending outbox rows for a dissolving tenant should be drained or
cancelled — the BILLING dissolve contributor handles billing-related ones; generic system effects
without a `TenantId` are unaffected. Audit this when wiring tenant dissolve.

**Known sharp edges (from ADR-007):** the enqueue MUST share the caller's transaction or atomicity is
lost; all handlers must be **idempotent** (at-least-once delivery); in-process single-poller is the
baseline — horizontal scale needs `SKIP LOCKED` claim semantics (built in here) or a real broker;
don't reach for Redis/Hangfire until multi-node actually forces it.
