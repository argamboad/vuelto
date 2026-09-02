# Stories — Outbound webhooks (`HOOKS`)

> One file per epic. The **outbound** integration half (PUBAPI is inbound): a tenant subscribes to events
> in their data and *their* systems get a signed POST when one fires. **Delivery is the transactional
> outbox pointed outward** (ADR-007) — durable, retried, atomic with the triggering change. **Config-gated,
> default off.** Design + constraints in **ADR-016**. Stories use Gherkin acceptance criteria.
> **Status: ✅ HOOKS-1 + HOOKS-2 shipped.** HOOKS-1 (`feat/hooks-1-outbound`) — subscriptions + signed
> outbox delivery + owner management. HOOKS-2 (`feat/hooks-2-delivery-log`) — a tenant-facing **delivery
> log** (one row per attempt, success/failure/status) + **replay** (re-enqueue a past delivery's exact
> payload). The user reversed the earlier "no customer-facing API" stance on 2026-07-01.

**Epic key:** `HOOKS`

**Prerequisites (external, before any code):**
- None to build. Reuses the **outbox** (ADR-007 ✅), Data Protection (encrypt the signing secret), RBAC
  (`.RequirePermission`), and the minimal-API feature-group + config-gate conventions (as in PUBAPI).

**Reuses:** `IOutbox`/`IOutboxHandler` + `OutboxDispatcher` (delivery/retry/backoff/dead-letter),
Data Protection (secret at rest), `HttpCurrentTenant`, `.RequirePermission(...)`.

---

### HOOKS-1 — Subscriptions, signed outbox delivery, owner management

**Status: ✅ Implemented** (`feat/hooks-1-outbound`). `WebhookSubscription : ITenantScoped` (url +
event types + **encrypted** signing secret revealed once; migration `AddWebhookSubscription`).
`IWebhookPublisher.PublishAsync(eventType, data)` (`src/Api/Services/`) fans out to active matching
subscriptions → one `"webhook"` **outbox** message each (staged on the caller's UoW). `WebhookOutboxHandler`
(`src/Infrastructure/Webhooks/`) loads the subscription, decrypts the secret, and POSTs the signed body via
`WebhookSender`; a non-2xx throws → outbox retry/backoff/dead-letter. Signature = HMAC-SHA256
(`WebhookSignature`, Core) in `X-Webhook-Signature: sha256=<hex>` + `X-Webhook-Id`/`X-Webhook-Event`.
Owner-only `/api/webhooks` (new `Permission.ManageWebhooks`): create/list/delete + synchronous **`/{id}/test`**
(ping, returns the endpoint status). All behind `Webhooks:Enabled` (default off → routes 404). Tests:
`tests/Core.Tests/WebhookSignatureTests.cs` + `tests/Api.Tests/Webhooks/` (subscription mgmt incl.
encrypted-secret; publisher fan-out; handler signs/POSTs, throws on non-2xx, no-op if gone); boot-verified
on (401 without auth) and off (404).

**As a** tenant owner
**I want** to register endpoints that get notified when events happen in my data
**So that** my systems integrate with the app without polling

**Acceptance criteria**

```gherkin
Scenario: Owner registers a webhook (secret shown once)
  Given webhooks are enabled and I am the tenant owner
  When I register an https endpoint for an event type
  Then I get a signing secret exactly once, and afterwards only the subscription metadata

Scenario: An event is delivered, signed, to matching subscriptions
  Given an active subscription for event "ping"
  When that event is published
  Then my endpoint receives a POST whose X-Webhook-Signature verifies with my secret
  And only subscriptions that chose that event type receive it

Scenario: Failed deliveries retry, then dead-letter
  Given my endpoint returns 500
  When the event is delivered
  Then the outbox retries with backoff and dead-letters after the cap (no data lost, no infinite loop)

Scenario: Removing a subscription stops delivery
  Given an event enqueued for a subscription I then delete
  When the delivery runs
  Then it is a no-op (not retried)

Scenario: Management + the surface are owner-only and off by default
  Given I am not the owner (or webhooks are disabled)
  When I call the webhook management routes
  Then I am refused (403) — or, when disabled, the routes do not exist (404)
```

**HOOKS-2 (✅ done, `feat/hooks-2-delivery-log`):** a tenant-facing **delivery log** — `WebhookDelivery`
(one row per attempt: event, success, status/error, and the sent body; **not** `ITenantScoped`, written
from the tenant-less dispatcher *and* the request-scoped send-test, read side filters by `TenantId`;
migration `AddWebhookDelivery`). The **async outbox** path records each attempt — a success row commits
atomically with the message's Sent flip, while a **failed** attempt is written through a fresh out-of-band
context so it survives the processor's rollback (**2026-08-24 fix**: staged failure rows were silently
discarded by the rollback, so the log only ever showed successes — blind exactly when an endpoint was
failing). The synchronous `/{id}/test` records its attempt too (`SendTestAsync`, success **and** failure) —
without it the log would be empty on the shipped template, since the sample app publishes no events (**fixed
2026-08-28, QA-API-06**). Owner routes `GET /api/webhooks/{id}/deliveries` (recent attempts) + `POST
/api/webhooks/deliveries/{id}/replay` (re-enqueue the exact stored payload — same event id so the receiver
dedups). Covered by `WebhookDeliveryLogTests`. **Still out of scope (candidate HOOKS-3):** a Blazor **management UI** (the API +
public OpenAPI doc make it usable headless — a UI for a default-off developer feature is lower value);
per-subscription rate limiting; automatic disable after N consecutive failures.
**Definition of done:** tests first; encrypted secret + one-time reveal; publish fans out to matching active
subs via the outbox; HMAC-signed deliveries; retry/dead-letter via the outbox; owner-only management;
**default-off strong gating** (404 when disabled); merged, app working; ADR-016 referenced.

---

## Slice plan (implementation map)

1. ✅ **Subscriptions + signed outbox delivery + owner management (HOOKS-1).** — DONE.
2. ✅ **Delivery log + replay (HOOKS-2).** — DONE. `WebhookDelivery` per-attempt record + view/replay
   endpoints. (Blazor management UI = candidate HOOKS-3.)

**Known sharp edges (from ADR-016):** delivery is the **outbox pointed outward** (don't build a second
mechanism); the secret is **encrypted** (needed in plaintext to sign); deliveries are **at-least-once** —
receivers dedup on `X-Webhook-Id`; **default off** with **strong gating**; managing subscriptions is
**owner-only**.
