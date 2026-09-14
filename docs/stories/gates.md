# Epic `GATES` — pre-launch gates

> **Epic key:** `GATES`. Two deployment-config gates that let a deployment run **private and
> free** before it is published: billing is switched off (nobody is sold anything), and account
> creation is restricted to a green list (nobody signs up just by finding the URL). Both are
> cleared on the day the app is published. **Decision record: ADR-027** (inherited from the platform).
>
> **Status: ✅ COMPLETE** (ported from the platform 2026-09-14, where GATES-1 + GATES-2 were built
> first). The design notes below are the platform's and are kept verbatim, because every decision in
> them applies here unchanged.

## Why this exists

The maintainer wants friends to test the apps before deciding whether to publish them free or
billed. Two things must be true while that is happening:

1. Nothing anywhere invites a tester to pay. There is no provider account behind the button yet.
2. A stranger who finds the URL cannot create an account. Knowing the app *exists* is fine and is
   explicitly **not** something this epic tries to hide — that would be a hosting-layer control and
   is not worth it for a friends test.

Both are properties of a **deployment**, decided before the process starts, which is why both are
configuration and not a runtime switch on `/admin`. See ADR-027 for the rejected alternative.

## The green-list rule (the part worth reading twice)

> The green list decides **who may found a household**. Inside a household owned by a green-listed
> person, they can do as they want — invite, promote to admin — bounded only by the seat cap.

Concretely, at account creation the caller is allowed when **either**:

- their verified email is on the green list (by address or by domain); **or**
- a valid pending `TenantInvitation` is addressed to that email **and the owner of that
  invitation's household is green-listed**.

The owner is checked, not the person who clicked invite, so a non-listed admin member can still
invite into their owner's household. A household whose owner is not green-listed can invite
nobody new into the app.

**Known and accepted:** `TenantService.ReHomeAsync` hands any departing member a fresh
tenant-of-one they own, so someone who arrived by invitation *can* end up owning a household.
Harmless — that household's owner is not green-listed, so it can pull nobody in.

---

### GATES-1 — Billing is off until a deployment turns it on

**As a** maintainer running a private test
**I want** billing switched off for the whole deployment
**So that** no tester is ever shown an upgrade path that has no provider account behind it

**Context / notes:** mirrors the PUBAPI/HOOKS config gates (ADR-015/016) — `Billing:Enabled`,
default **off**. `PlanCatalog.Get` already falls back to Free for an absent/unknown plan key, so
the economics need no new code: with billing off every tenant is Free. Consequence accepted: while
off, nobody holds `Entitlements.ProFeature`.

The Free seat limit moves **3 → 5** in the same slice. Three is exactly one family (the
maintainer's household is three people) with zero headroom, and a *pending* invitation already
consumes a seat.

**Acceptance criteria**

```gherkin
Scenario: the billing surface does not exist when the gate is off
  Given a deployment with Billing:Enabled unset
  When a signed-in member requests any /api/billing route or the provider webhook
  Then the API answers 404
  And the client renders no billing link in the header
  And navigating to /billing lands on the home page instead

Scenario: the billing surface is fully present when the gate is on
  Given a deployment with Billing:Enabled=true
  When a signed-in member opens /billing
  Then the plan summary renders and checkout can be started

Scenario: no upgrade wording survives with the gate off
  Given a deployment with Billing:Enabled unset
  And a household that has reached its seat limit
  When the owner tries to invite one more member
  Then the refusal says the seat limit is reached
  And it does not offer to upgrade the plan

Scenario: an invitee meeting a full household is not sold anything
  Given a deployment with Billing:Enabled unset
  And a household already at its seat limit
  When someone opens a still-valid invitation to it
  Then the page says the household is full
  And it does not tell them to ask the owner to upgrade

Scenario: the gate is closed under empty configuration
  Given an empty IConfiguration
  When BillingSettings is bound from it
  Then Enabled is false
```

**Out of scope:** removing billing code, changing the provider abstraction, per-tenant billing
toggles, and any runtime/admin switch (ADR-027 rejects it).

**Definition of done:** tests written first; API 404s on every billing route with the gate off;
both resx files carry the billing-off copy at parity; `ConfigPostureTests` covers the new gate;
Postman + `.env.example` + QA plan updated in the same PR.

---

### GATES-2 — Only the green list may found a household

**As a** maintainer running a private test
**I want** account creation restricted to people I listed, plus whoever those people invite
**So that** a stranger who finds the URL cannot get in, without me having to hide the deployment

**Context / notes:** the gate sits at the single account-creation choke point,
`UserService.CreateUserWithTenantAsync`, so every user-minting path is covered at once: magic
link, OTP, web OAuth, and native OAuth. **It gates creation only, never sign-in of an existing
account** — otherwise editing the list later would lock out people who already have data.

Two traps that shaped the design and should not be re-derived:

- **Ordering.** Invitations are redeemed by someone who is *already signed in* (`Join.razor` sends
  an anonymous visitor to `/login` first), so the gate fires **before** any invitation token is
  presented. Carrying the token through sign-in was rejected: it would have to survive an OAuth
  round trip and a magic link opened in a different mail client on a different device. The gate
  matches on `TenantInvitation.InvitedEmail` instead, which is stored normalized lower-case.
- **No enumeration oracle.** The gate deliberately does **not** fire when a magic link or OTP is
  *issued*. Refusing at issue time would require knowing whether the address already has an
  account, which turns the login form into a "does this person use the app" probe. The cost is
  that a non-listed visitor learns they are not invited only after entering their code.

The invitation lookup is a pre-auth cross-tenant read, so it goes through
`IRepository<T>.QueryAllTenants()` (the sanctioned ADR-003 hatch, auto-tagged for the ADR-020 RLS
backstop) — the same shape as the pre-auth API-key lookup in `ApiKeyService.AuthenticateAsync`.

**Acceptance criteria**

```gherkin
Scenario: signup is open when no green list is configured
  Given Signup:AllowedEmails and Signup:AllowedDomains are both unset
  When any verified email completes sign-in for the first time
  Then an account and its household are created

Scenario: a green-listed address may found a household
  Given Signup:AllowedEmails contains the caller's address
  When they complete sign-in for the first time
  Then an account and its household are created

Scenario: a stranger is refused
  Given a green list that does not contain the caller's address
  And no invitation is addressed to it
  When they complete sign-in for the first time
  Then no account is created
  And they are told the app is in private testing

Scenario: someone invited into a green-listed owner's household may sign up
  Given a green list containing the household owner's address
  And a valid pending invitation addressed to the caller
  When the caller completes sign-in for the first time
  Then an account is created

Scenario: an invitation from a household whose owner is not green-listed does not admit anyone
  Given a household whose owner is not on the green list
  And a valid pending invitation it issued to the caller
  When the caller completes sign-in for the first time
  Then no account is created

Scenario: an expired or already-accepted invitation admits nobody
  Given a green list that does not contain the caller's address
  And the only invitation addressed to them is expired or already accepted
  When they complete sign-in for the first time
  Then no account is created

Scenario: an existing account signs in regardless of the green list
  Given an account that already exists
  And a green list that does not contain its address
  When it signs in
  Then the sign-in succeeds

Scenario: the refusal reaches every sign-in path
  Given a green list that does not contain the caller's address
  When they complete OTP, magic-link, web-OAuth or native-OAuth sign-in for the first time
  Then each path reports the refusal in its own idiom and creates nothing
```

**Out of scope:** per-tenant invite codes, promotional codes (rejected — a code bound to an email
proves nothing the magic link does not already prove), hiding the deployment from the public
internet, and any self-serve way to edit the list from inside the app.

**Definition of done:** tests written first; a refusal test per user-minting path; existing
accounts provably unaffected; `ConfigPostureTests` covers the new settings; `.env.example` + QA
plan updated in the same PR.

---

## What the build surfaced (worth knowing before porting)

- **The production Stripe-key guard had to be relaxed.** It refused to boot outside Development without
  `Billing__Stripe__SecretKey`, which would have blocked the exact deployment this epic is for. Its
  stated reason — the fake provider trusts a literal webhook signature and the webhook is anonymous —
  evaporates when the gate is off, because the webhook route does not exist. It now fires only when
  billing is ON, and `BillingControllers_AreAllGated` is what keeps "no reachable webhook" true.
- **Gating is structural, not a filter.** `BillingGateConvention` removes the controllers from the MVC
  application model, so a billing endpoint nobody has written yet is gated by construction.
- **The client needed a probe.** `GET /api/features` (anonymous) is how the header and the seat-limit
  copy learn the gate state. It is convenience; the API stays the authority.
- **Seat tests now read the limit from the catalog** instead of copying it, so the 3 → 5 change did not
  need five separate edits and the next re-tune will not either.
- **A flaky test was ours, not the product's:** the magic-link case did not URL-escape the token, so it
  failed only when a generated token happened to contain a character a query string re-reads.

## Launch-day checklist (what "publishing" means)

1. Set `Billing__Enabled=true` (or leave it off to publish free).
2. Clear `Signup__AllowedEmails` / `Signup__AllowedDomains`.
3. Restart the service. Both gates read configuration at startup.
