# Stories — Billing & Subscriptions

> One file per epic. Adds monetization to the platform: a provider-abstracted billing seam
> (`IBillingProvider`), a Stripe reference implementation, plan-tier **entitlements** (feature
> flags keyed to plan) and **quotas** (countable limits). **Status: ✅ COMPLETE** — **BILLING-1–8
> shipped** (entitlement gate; Checkout; webhook → subscription projection; Customer Portal; seat +
> metered-usage quotas; trial/dunning lifecycle; **dissolve cleanup** — cancel the provider sub + wipe
> the projection when a tenant is dissolved). The core loop is closed and self-serve manage/cancel
> works (changes flow back through the webhook). **BILLING-5** (`feat/billing-5-quotas`): `IQuotaService`
> — seats (members + pending invites vs the plan's `SeatLimit`) enforced on the invite path
> (→ 402 `seat_limit_reached`) + metered usage (`TryConsumeAsync`, monthly `UsageCounter`); limits in
> `PlanCatalog` (null = unlimited). **BILLING-6** (`feat/billing-6-dunning`): dunning notifications to the
> owner on past_due/canceled transitions + a `SubscriptionLapseSweepJob` one-time nudge for lapsed
> periods (via NOTIFY). **BILLING-7** (`feat/billing-7-dissolve-cleanup`): `BillingDataContributor` — wipe
> the projection + cancel the provider subscription (outbox) when a tenant is dissolved. *Remaining
> follow-up (optional):* advance trial-ending nudge. Design decision and constraints in **ADR-006**.
> Stories use Gherkin acceptance criteria.

> **2026-07-15 — billing edge-case correctness (v3 audit LB-BILL-1/3/4).** (1) The webhook **recency guard**
> now rejects only *strictly* older events (`OccurredAt < last`, was `<=`) — Stripe's `Created` is
> whole-second, so two DISTINCT events in the same second (checkout `created`+`updated`, a rapid plan change)
> must both apply; exact redelivery is still caught by the inbox EventId. (2) **Dunning** fires only on a
> transition *out of a granting status* (`SubscriptionStatus.IsGranting`) — a first-ever event carrying
> `past_due`/`canceled` (failed first invoice, abandoned checkout) no longer duns a tenant that never had a
> live subscription. (3) `QuotaService` first-consume retry now catches **only** the unique-violation
> (`SqlState 23505`); any other `DbUpdateException` propagates instead of being misread as a benign insert
> race (which could spuriously deny a request with headroom). Tests in `BillingWebhookHandlerTests` +
> `QuotaServiceTests`.

**Epic key:** `BILLING`

**Prerequisites (external, before any code):**
- A **Stripe account** (test mode is enough to build and ship the whole epic — no real charges).
- The **Stripe CLI** installed for local webhook forwarding (`stripe listen --forward-to
  https://localhost:7160/api/billing/webhook`) and event simulation (`stripe trigger`).
- A defined **plan catalog** — at least Free + one paid tier — with Stripe Product/Price ids. The
  plan catalog is code/config, **not** tenant data (see ADR-006).
- The **outbox/inbox** slice (`JOBS-1`/`JOBS-2`) merged first — webhook idempotency rides on it.
- Package: `Stripe.net` (latest stable, .NET 10 — no previews, ADR-C10).

**Billing without real money — the sandbox/fake stack (ADR-006):**
| Layer | Tool | Used by |
|-------|------|---------|
| Unit | `FakeBillingProvider` (in-memory, implements `IBillingProvider`) | `Core.Tests`, `Api.Tests` handler logic |
| Integration | **stripe-mock** (Stripe's official mock server, offline) | `Api.Tests` — request/response shape, no network |
| E2E / manual | **Stripe test mode + Stripe CLI** (`listen` / `trigger`) | `E2E.Tests`, local QA — real webhook round-trip |
| Lifecycle | **Stripe Test Clocks** | trial-end → renewal → dunning, simulated deterministically |

This is the Mailpit-for-billing analogue (ADR-C13): traps everything locally, zero real cost.

---

### BILLING-1 — Plan catalog + entitlement gate

**Status: ✅ Implemented** (`feat/billing-1-entitlement-gate`). Entity `src/Core/Entities/Subscription.cs`
(`ITenantScoped`, unique per tenant; Stripe ids nullable until BILLING-2); catalog
`src/Core/Billing/PlanCatalog.cs` (`PlanKeys`, `Entitlements`, fail-closed `Get`); gate
`src/Core/Abstractions/IEntitlementService.cs` + `src/Api/Services/EntitlementService.cs`
(fails closed to Free) + `src/Api/Features/EntitlementEndpointExtensions.cs` (`.RequireEntitlement(key)`
→ 402); migration `AddSubscription`; tests `tests/Api.Tests/Billing/`. **Scope note:** `IBillingProvider`
+ `FakeBillingProvider` were deferred to **BILLING-2**, where the first provider call (Checkout) actually
needs them — BILLING-1 has no payment path, so adding them now would be untested dead code.

**As a** product owner
**I want** features gated by the tenant's plan tier
**So that** paid capabilities are reserved for paying tenants and I can sell tiers

**Context / notes:** foundation slice — no payment yet, just the access seam. A `Subscription`
projection (ADR-006) defaults every tenant to **Free** (fail-closed) until BILLING-2 attaches a
paid plan. `IEntitlementService.Has(tenant, "feature-key")` reads the plan from the subscription
projection; a `RequireEntitlement("feature-key")` endpoint filter mirrors
[`MapTenantFeatureGroup`](../../src/Api/Features/FeatureEndpointExtensions.cs) so a slice gates
itself declaratively. Entitlement checks are **server-side only** — never trust the client.

**Acceptance criteria**

```gherkin
Scenario: Free tenant is denied a pro-gated endpoint
  Given I am the owner of a tenant on the Free plan
  When I call an endpoint gated by RequireEntitlement("pro-feature")
  Then I receive 402 Payment Required (not 403/500)
  And the response names the entitlement and a link to upgrade

Scenario: Pro tenant is allowed the same endpoint
  Given my tenant has an active Pro subscription
  When I call the pro-gated endpoint
  Then the request is allowed and handled normally

Scenario: Missing or expired subscription falls back to Free (fail-closed)
  Given my tenant has no subscription row, or a past_due/canceled one
  When an entitlement is checked
  Then the tenant is treated as Free, never as the higher tier
```

**Out of scope:** taking payment (BILLING-2); quotas/counters (BILLING-5).
**Definition of done:** tests first (TDD); `IEntitlementService` + the fail-closed default unit-tested;
the `RequireEntitlement` filter integration-tested (Free→402, Pro→200); tenant-scoping verified;
merged, app working; ADR-006 referenced.

---

### BILLING-2 — Subscribe via Stripe Checkout

**Status: ✅ Implemented** (`feat/billing-2-checkout`, placement corrected in `refactor/billing-controller`).
Abstraction `src/Core/Abstractions/IBillingProvider.cs` (+ `BillingCheckoutRequest`/`BillingCheckoutSession`);
`src/Infrastructure/Billing/` — `StripeBillingProvider` (Stripe.net 52, subscription-mode Checkout,
tenant id in `ClientReferenceId`+metadata), `FakeBillingProvider` (offline; also the dev fallback when
no Stripe key is set), `StripeSettings`. The HTTP surface is a **platform controller**
`src/Api/Controllers/BillingController.cs` (`POST /api/billing/checkout`, owner-only via
`TenantApiControllerBase`) + `IBillingService` (`src/Api/Services/`) — **not** a `Features/` slice
(billing is chassis, not an app feature; see the ADR-006 + ADR-004 amendments). Config
`Billing__Stripe__SecretKey` + `Billing__Stripe__Prices__pro` in `.env.example`. Tests
`tests/Api.Tests/Billing/` (service: plan logic; controller: owner gate). **Test-coverage note:**
acceptance criteria are covered offline (`FakeBillingProvider`); the live SDK round-trip is left to the
E2E layer (Stripe test mode / stripe-mock) per the DoD — the planned stripe-mock Testcontainer test was
deferred (Testcontainers 4.12 API friction; not worth blocking for bonus coverage), with a
deterministic price-resolution guard test kept in its place.

**As a** tenant owner
**I want** to upgrade my tenant to a paid plan
**So that** my tenant unlocks paid features

**Context / notes:** money mutations happen **on Stripe**, never in our forms — we redirect to
**Stripe Checkout** and store no card data (PCI scope stays SAQ-A, ADR-006). The handler creates a
Checkout Session via `IBillingProvider.CreateCheckoutSession(tenant, priceId)` and returns its URL;
our `Subscription` row only becomes Active when the resulting **webhook** lands (BILLING-3), not on
the redirect back. Only the **owner** may purchase (`TenantRoles.Owner`).

**Acceptance criteria**

```gherkin
Scenario: Owner starts a checkout for the Pro plan
  Given I am the owner of a Free-plan tenant
  When I choose "Upgrade to Pro"
  Then I am redirected to a Stripe Checkout session for the Pro price
  And the session carries my tenant id so the webhook can reconcile it

Scenario: Non-owner cannot purchase
  Given I am a member (not owner) of the tenant
  When I attempt to start a checkout
  Then I receive 403 and no Checkout session is created

Scenario: Checkout success does not grant access until the webhook confirms
  Given I completed Stripe Checkout
  When I return to the app before the webhook is processed
  Then my tenant is still Free until the subscription.created/updated event is applied
```

**Out of scope:** webhook processing (BILLING-3); portal/cancel (BILLING-4).
**Definition of done:** tests first; owner-only guard + tenant-id propagation unit-tested; the
`FakeBillingProvider` drives handler tests, `stripe-mock` the integration test; tenant-scoping
verified; merged, app working.

---

### BILLING-3 — Webhook keeps the subscription projection current

**Status: ✅ Implemented** (`feat/billing-3-webhook`). Provider gains `IBillingProvider.ParseWebhookEvent`
(+ `BillingWebhookEvent`/`BillingWebhookSignatureException`); `StripeBillingProvider` verifies via
`EventUtility.ConstructEvent` and maps the Stripe subscription (tenant id from the metadata stamped at
checkout, plan from the price, fail-closed status); `FakeBillingProvider` parses a normalized-event JSON
for offline tests. Flow in `src/Api/Services/BillingWebhookHandler.cs`: **verify → `IInbox.TryClaimAsync`
dedup (ADR-007) → `EnterTenant` (ADR-003) → upsert `Subscription`** (claim + write in one transaction).
System endpoint `src/Api/Controllers/BillingWebhookController.cs` (`POST /api/billing/webhook`, anonymous,
signature-gated). Config `Billing__Stripe__WebhookSecret`. Tests `tests/Api.Tests/Billing/BillingWebhookHandlerTests.cs`.
**No `IgnoreQueryFilters` in the flow** — the write is tenant-scoped via the entered context.

**As a** the platform
**I want** Stripe webhooks to drive our subscription state
**So that** entitlements reflect reality (paid, past_due, canceled) without polling

**Context / notes:** Stripe is the **system of record for money**; our DB holds a **projection**
(ADR-006). The webhook endpoint verifies the Stripe signature, then hands the event to the **inbox**
(`JOBS-2`) for at-least-once **idempotent** processing — dedupe by Stripe event id, tolerate
out-of-order delivery. This endpoint is **unauthenticated** (Stripe calls it) but signature-gated,
and is rate-limit-exempt from the per-IP passwordless policy. Applying an event upserts the
`Subscription` (status + current_period_end + plan).

**Acceptance criteria**

```gherkin
Scenario: subscription.created activates the tenant's plan
  Given a valid signed customer.subscription.created event for my tenant's Pro price
  When the webhook is processed
  Then my tenant's Subscription projection is Active on Pro
  And pro-gated endpoints become allowed

Scenario: Duplicate delivery is ignored (idempotent)
  Given an event id that has already been processed
  When the same event is delivered again
  Then it is acknowledged with no duplicate state change

Scenario: Invalid signature is rejected
  Given a webhook payload with a bad or missing Stripe signature
  When it hits the endpoint
  Then it is rejected 400 and nothing is applied

Scenario: subscription.deleted / past_due downgrades to Free (fail-closed)
  Given my tenant was Active on Pro
  When a canceled or past_due event is applied
  Then entitlements fall back to Free
```

**Out of scope:** the inbox mechanism itself (JOBS-2 owns it); dunning emails (BILLING-6).
**Definition of done:** tests first; signature verification + idempotency + each state transition
unit/integration-tested with `stripe-mock` and `stripe trigger`; tenant reconciliation (event →
correct tenant) verified; merged, app working.

---

### BILLING-4 — Manage subscription via Customer Portal

**Status: ✅ Implemented** (`feat/billing-4-portal`). `IBillingProvider.CreatePortalSessionAsync` (+
`BillingPortalRequest`/`BillingPortalSession`); `StripeBillingProvider` via
`Stripe.BillingPortal.SessionService`; `FakeBillingProvider` for offline tests. `BillingService.CreatePortalAsync`
reads the current tenant's `Subscription.StripeCustomerId` (stored by the webhook) → no subscription ⇒
clean 400. `POST /api/billing/portal` on the platform `BillingController` (owner-only via the base gate).
Tests in `tests/Api.Tests/Billing/` (service: with/without subscription; controller: owner→200,
owner-no-sub→400, member→403). All changes made in the portal flow back through the BILLING-3 webhook —
no new state logic.

**As a** tenant owner
**I want** to change or cancel my plan
**So that** I control my own billing without contacting support

**Context / notes:** reuse **Stripe Customer Portal** (a redirect, like Checkout) rather than
building plan-change/cancel UI. `IBillingProvider.CreatePortalSession(tenant)` returns the URL; all
resulting changes flow back through BILLING-3's webhook. Owner-only.

**Acceptance criteria**

```gherkin
Scenario: Owner opens the billing portal
  Given I am the owner of a tenant with a Stripe customer
  When I click "Manage billing"
  Then I am redirected to the Stripe Customer Portal for my customer

Scenario: Cancellation propagates via webhook
  Given I cancel my subscription in the portal
  When Stripe sends the resulting subscription.updated/deleted event
  Then my tenant downgrades to Free per BILLING-3
```

**Out of scope:** proration math (Stripe owns it); invoices UI.
**Definition of done:** tests first; owner-only guard tested; portal session creation tested via
`FakeBillingProvider`/`stripe-mock`; merged, app working.

---

### BILLING-5 — Seat & usage quotas — ✅ Implemented (`feat/billing-5-quotas`)

> **Shipped.** `IQuotaService` (`src/Api/Services/QuotaService.cs`) resolves the tenant's plan like
> `EntitlementService` (fail-closed to Free). **Seats** = members + pending invites vs `Plan.SeatLimit`;
> checked in `TenantInvitationService.CreateAsync` for new invites → `InviteCreateStatus.SeatLimitReached`
> → **402 `seat_limit_reached`** (Household invite UI shows an upgrade message). **Metered usage** =
> `TryConsumeAsync(key)` against a monthly `UsageCounter` (per `{tenant, key, yyyy-MM}` — self-resetting,
> no sweep job); returns false without incrementing at the cap. Limits are `PlanCatalog` example data
> (Free seats=3/export=3, Pro seats=10/export=100); **null/absent = unlimited** so it's inert until set.
> Tests: `tests/Api.Tests/Billing/QuotaServiceTests.cs` (seat boundaries incl. pending-invite counting +
> upgrade; usage within/at-limit/unlimited/month-reset; invite-flow 402). `TryConsumeAsync` is the seam —
> call it at any metered action (e.g. an export) to enforce a per-month cap.

**As a** the platform
**I want** plan-tier quotas enforced (e.g. seats, metered usage)
**So that** tiers have teeth beyond feature on/off

**Context / notes:** quotas are **per-tenant, persisted counters** — a different mechanism from the
per-IP request throttle in [`RateLimiting.cs`](../../src/Api/Configuration/RateLimiting.cs) (don't
conflate them, ADR-006). **Seats** = count of `TenantMembership` for the tenant vs the plan's seat
limit; checked when inviting (extends
[`HouseholdInvitationsController`](../../src/Api/Controllers/HouseholdInvitationsController.cs)).
**Metered usage** = an incrementing counter checked by `IQuotaService.TryConsume(tenant, key, n)`.

**Acceptance criteria**

```gherkin
Scenario: Inviting beyond the seat limit is blocked
  Given my Free plan allows 3 seats and my tenant already has 3 members
  When I invite a 4th member
  Then the invite is rejected 402 with an upgrade prompt
  And no TenantInvitation is created

Scenario: Upgrading raises the seat limit
  Given my tenant upgrades to Pro (10 seats)
  When I invite a 4th member
  Then the invite succeeds

Scenario: Metered action is denied once the quota is exhausted
  Given my plan's monthly quota for "export" is 5 and I have used 5
  When I attempt a 6th export
  Then it is denied 402 and the counter is not incremented
```

**Out of scope:** billing for overages (metered/usage-based pricing is a later slice);
quota-reset scheduling lives in `JOBS-3`.
**Definition of done:** tests first; seat-count and `IQuotaService` logic unit-tested at boundaries
(at-limit, over-limit, after-upgrade); tenant-scoping verified; merged, app working.

---

### BILLING-6 — Trial & dunning lifecycle — ✅ Implemented (`feat/billing-6-dunning`)

**As a** product owner
**I want** trials and failed-payment (dunning) handling
**So that** I can offer trials and recover failed renewals

**Context / notes:** lifecycle transitions (trial → active → past_due → canceled) are driven by
Stripe (the projection already reflects them via BILLING-3) and validated with **Stripe Test Clocks**
(fast-forward simulated time — ADR-006). BILLING-6 adds the **owner-facing reaction**: a **dunning
notification** on the bad transitions + a **scheduled lapse sweep** for periods that end without a
webhook. Dunning goes through the **notification center** (NOTIFY, in-app + outbox email per prefs); the
sweep is a scheduled job (JOBS-3).

> **Shipped.** `IBillingNotifier` (`src/Api/Services/BillingNotifier.cs`) notifies the tenant **owner**
> via `INotificationService.NotifyAsync` (in-app row + outbox email). The **webhook handler** compares the
> pre-event status and, on a **transition into `past_due` or `canceled`**, notifies (same-status
> redeliveries don't re-notify; the inbox already dedups by event id). The **`SubscriptionLapseSweepJob`**
> (`IScheduledJob`, every 6h) scans all tenants (`QueryAllTenants`) for subscriptions still active/trialing
> whose `CurrentPeriodEnd` has passed, sends a one-time "expired" nudge, and stamps a new
> `Subscription.LapseNotifiedAt` (migration `AddSubscriptionLapseNotifiedAt`) so it fires once per lapse —
> **without fabricating a status** (Stripe stays source of truth; entitlements already fail closed on a
> lapsed period). Tests: `BillingWebhookHandlerTests` (past-due notifies once; no-change doesn't) +
> `SubscriptionLapseSweepJobTests` (nudge-once + stamp; in-period ignored).

**Acceptance criteria**

```gherkin
Scenario: A failed payment notifies the owner (dunning)
  Given my subscription was active
  When Stripe reports the renewal payment failed (status past_due)
  Then the household owner gets a notification to update their payment method
  And a duplicate/again-past_due delivery does not notify a second time

Scenario: A cancellation notifies the owner
  Given my subscription was active
  When it transitions to canceled
  Then the owner is notified and access falls back to Free (fail-closed)

Scenario: A lapsed period is swept and nudged once
  Given a subscription still marked active/trialing whose period end has passed
  When the lapse sweep runs
  Then the owner is nudged once to resubscribe
  And a later sweep does not nudge again for the same lapse
```

**Out of scope:** advance "trial ends in N days" nudges (needs a Stripe `trial_will_end` event kind or a
dedup field per period — a small follow-up); reimplementing Stripe's own retry schedule / card-failure
emails (**Stripe Smart Retries** owns that). *(Dissolve cleanup is BILLING-7 — done.)*
**Definition of done:** tests first; dunning on past_due/canceled transitions (once, no spam); lapse sweep
nudges once + records it; Stripe status never fabricated; merged, app working; ADR-006 referenced.

---

## Slice plan (implementation map — when undeferred)

Ordered, each a mergeable vertical slice. TDD throughout (write the failing test first). **JOBS-1/2
must land first** (ADR-007) — billing webhooks depend on the inbox.

1. ✅ **Plan catalog + entitlement gate (BILLING-1).** — DONE.
   - `Core/Entities/Subscription.cs : ITenantScoped` (plan key, status, stripe ids [nullable],
     current_period_end) + EF config + `AddSubscription` migration. Default-absent ⇒ Free.
   - Plan catalog in code (`Core/Billing/PlanCatalog.cs`): plan key → entitlements (seat/usage limits
     come in BILLING-5).
   - `IEntitlementService` + `EntitlementService` (fail-closed) + `.RequireEntitlement(key)` endpoint
     filter (sibling of `FeatureEndpointExtensions`), 402 on deny.
   - **Tests:** fail-closed resolution (no-sub / past_due / canceled / lapsed → Free; active/trialing →
     granted) + filter 402-vs-allow via TestServer.
   - **Deferred to slice 2:** `IBillingProvider` + `FakeBillingProvider` — first needed for the Checkout
     call, so they land with BILLING-2 (no payment path exists in BILLING-1 to test them against).
2. ✅ **Stripe reference impl + Checkout (BILLING-2).** — DONE.
   - `IBillingProvider` (Core) + `StripeBillingProvider` (`Stripe.net` 52) + `FakeBillingProvider`
     (offline + dev fallback). Registered in `ServiceCollectionExtensions` guarded by
     `Billing:Stripe:SecretKey` presence (same pattern as the OAuth providers); no key ⇒ fake.
   - **Platform** `BillingController : TenantApiControllerBase` (`/api/billing/checkout`, owner-only
     via the base gate) + `IBillingService`. Billing is chassis, so it's a controller, not a
     `Features/` slice (ADR-006/ADR-004 amendments).
   - `.env.example`: `Billing__Stripe__SecretKey`, `Billing__Stripe__Prices__pro`.
     (`Billing__Stripe__WebhookSecret` lands with BILLING-3.)
   - **Tests:** owner-only / member-403 / no-membership / invalid-plan / no-access-until-webhook +
     tenant-id-in-request, all via `FakeBillingProvider`; price-resolution guard for the Stripe impl.
     The `stripe-mock` integration test is deferred to E2E (see the BILLING-2 status note above).
3. ✅ **Webhook + projection (BILLING-3).** — DONE. Anonymous signed endpoint
   (`BillingWebhookController`) → verify → inbox dedup (JOBS-2) → `EnterTenant` → upsert via the normal
   tenant-scoped path (no escape hatch). Tests: signature reject, idempotent dedup, activate, fail-closed
   cancel, tenant isolation (offline via `FakeBillingProvider`); live verification via `stripe trigger`
   at E2E.
4. ✅ **Customer Portal (BILLING-4).** — DONE. `POST /api/billing/portal` (owner-only) → portal redirect
   from the tenant's stored Stripe customer id; changes reconcile via the BILLING-3 webhook.
5. ✅ **Quotas (BILLING-5).** — DONE. `IQuotaService` (seats = members + pending invites vs
   `Plan.SeatLimit`, enforced on the invite path → 402; metered usage via `TryConsumeAsync` +
   monthly `UsageCounter`); limits are `PlanCatalog` data (null = unlimited).
6. ✅ **Trial/dunning (BILLING-6).** — DONE. `IBillingNotifier` dunning on webhook transitions into
   past_due/canceled (once, no spam) + `SubscriptionLapseSweepJob` (JOBS-3) one-time "expired" nudge for
   periods that lapse without a webhook (records `LapseNotifiedAt`, never fabricates status). Notifications
   ride NOTIFY (in-app + outbox email). *Follow-up:* advance trial-ending nudge.
7. ✅ **Dissolve cleanup (BILLING-7).** — DONE. `BillingDataContributor` wipes the `Subscription`
   projection on tenant dissolve and **cancels the provider subscription** (via a `"billing.cancel"`
   outbox message → `IBillingProvider.CancelSubscriptionAsync`), so a deleted tenant stops being billed.
   `HasDataAsync` = false (billing isn't abandonable content); export gains a `billing` section.
8. ✅ **Billing page (BILLING-8, 2026-07-03, `feat/billing-8-billing-page`).** — DONE. The owner-facing
   UI billing lacked (dunning notifications already deep-linked to `/billing`): `GET /api/billing`
   summary (owner-only `ManageBilling`; plan fail-closed via `EntitlementService.ResolvePlanKey`, raw
   status, `CurrentPeriodEnd`, seats via `IQuotaService.GetSeatUsageAsync`, `has_subscription` gating
   the portal button) + `Billing.razor` at `/billing` (plan card, seats, Upgrade→checkout redirect,
   Manage→portal redirect, member-facing owner-only notice) + `Nav_Billing` header link; EN/ES.
   E2E `BillingJourneyTests`: the full upgrade loop with **no Stripe** — FakeBillingProvider checkout
   URL stubbed via Playwright routing (tenant id parsed from it), provider webhook POSTed exactly as
   Stripe would (PascalCase body, `Stripe-Signature: valid`) through the real verify → inbox dedup →
   `EnterTenant` → projection path, page lands on pro/10-seats/portal. QA-BILL-01/02; ADR-006 addendum.

**Known sharp edges (from ADR-006):** webhooks are at-least-once and out-of-order (idempotency is
mandatory — needs JOBS-2); never grant access on the Checkout redirect, only on the webhook; the DB
is a **projection**, Stripe is the source of truth for money; entitlement checks are server-side and
fail-closed; quotas ≠ rate limits. Budget for Stripe dashboard setup (products/prices/webhook
endpoint), not just code.

---

### BILLING-9 — Seat quota re-checked when an invitation is accepted

**As a** platform operator
**I want** invitation acceptance to respect the tenant's *current* seat limit
**So that** a downgrade (dunning lapse, cancellation, admin comp revert) can't be bypassed by
accepting invitations issued while the tenant was on a bigger plan

**Context / notes:** BILLING-5 enforces seats only at invitation **creation** (pending invites
reserve seats, so the cap holds while the plan is stable). But nothing sweeps pending invites on a
downgrade and `AcceptAsync` never re-checked — so Pro (10 seats) → invite 7 → drop to Free (3)
left 7 valid invites that could each still join, actively growing the tenant past its cap
(found 2026-07-14 while reasoning about the ADR-021 comp/revert writes; applies equally to real
Stripe downgrades). The accept itself is **seat-neutral** — the joiner consumes the seat their
pending invite reserved — so the rule is "already over the limit" (`CanAdd(0)`), NOT "can add one
more": accepts at exactly the cap stay allowed, and the check only bites after a downgrade.
Existing members are never evicted (over-cap tenants are merely frozen for growth, matching the
invite-path behavior). The check runs inside `EnterTenant(invitation.TenantId)` — the caller's JWT
still carries their old tenant, and the quota must count the invitation's tenant (same trusted
contract as the accept's conditional token flip; ADR-020-compatible). ADR-006 addendum.

**Acceptance criteria**

Scenario: accept blocked when the tenant is over its downgraded cap
  Given a tenant that was Pro and invited past the Free seat limit
  And the subscription has since lapsed or been reverted to Free
  When an invitee redeems a still-valid invitation token
  Then the accept is refused with 402 "seat_limit_reached"
  And the join page shows a "household is full" message (EN/ES)
  And no membership was moved and the invitation stays pending

Scenario: accept at exactly the cap still works (seat-neutral swap)
  Given a Free tenant with 2 members and 1 pending invitation (3/3 seats used)
  When the invitee redeems the token
  Then they join and the tenant has 3 members

Scenario: the blocked invite self-heals on upgrade
  Given an accept was refused with seat_limit_reached
  When the tenant upgrades back to a plan with room
  Then redeeming the same (unexpired) token succeeds

**Out of scope:** revoking/sweeping pending invites on downgrade (destroys owner-created state and
re-upgrading would force re-inviting — the accept-time check self-heals instead); evicting members
from over-cap tenants; notifying the owner when an accept is refused (candidate follow-up via NOTIFY).
**Definition of done:** tests first (service-level over-cap/at-cap/heal on the Postgres harness; E2E
webhook-downgrade journey on the fake provider); 402 mapped and rendered on /join; QA plan + Postman
updated in the same PR; merged, app working.
