# Stories — E2E journey coverage for platform UI (`E2E`)

> One file per epic. Closes the Playwright gap for platform epics that shipped real UI but were
> built unit/integration-first: **RBAC roster management**, the **billing seat-quota** surface, and
> the **notification center/preferences**. Deliberately selective — headless machinery (webhooks,
> outbox, health checks, Stripe money paths) stays at the integration layer where it's already
> covered; `/health` liveness belongs to the DEPLOY-3 smoke step, not a browser test. Stories use
> Gherkin acceptance criteria. **Status: ✅ COMPLETE — E2E-1..5** (4 and 5 added after review
> showed magic-link + destructive flows were automatable after all).
>
> **Current suite size: 34 tests** (v3 audit T59 reconcile, 2026-07-27 — per the CI `e2e` job).
> The epic itself grew the suite 7→26; later slices kept adding: NATIVE-4b (→28), NATIVE-3 (→29),
> THEME-1 (→31), PREFS-1 (→32), BILLING-8 (→33), and v3 T45c's locale-mismatch × invite-acceptance
> journey (→34). This line is the running total the historical arrows elsewhere add up to — update
> it when a slice adds a journey.

**Epic key:** `E2E`

**Prerequisites (before any code):**
- **v2 audit remediation approved/landed first** (`docs/audits/v2-2026-07/AUDIT_TASKS.md`) — these
  journeys drive auth/tenancy surfaces the remediation may touch; don't write them against code
  about to change.
- No new packages, no new services. Reuses the existing harness: `E2ETestBase`, the Page Object
  Model (`tests/E2E.Tests/Pages/`), Mailpit (`Mailpit.cs`) for OTP sign-in and invitation emails,
  and the running dev stack per `tests/E2E.Tests/README.md`.

**Scope guardrails (what this epic deliberately does NOT do):**
- **No Stripe.** Checkout/Portal round-trips stay covered by
  `tests/Api.Tests/Billing/*` (webhook handler, entitlements, dunning). At the time this epic ran
  there was **no billing UI page** — the seat-limit message on the invite flow was the only billing
  E2E surface (E2E-2). **BILLING-8 later built `/billing`** and added `BillingJourneyTests`: the full
  upgrade loop with the FakeBillingProvider (stubbed checkout URL + a real webhook POST), still no
  Stripe involved.
- **No health/observability browser tests.** `/health` verification is the DEPLOY-3 deploy-smoke
  concern (ADR-017).
- **No E2E for config-gated-off surfaces** (PUBAPI, HOOKS, ADMIN console) or destructive GDPR
  flows — integration tests + `docs/QA_TEST_PLAN.md` cover them.

**Conventions that apply to every story below:**
- TDD: write the failing Playwright test first; the failure drives adding the missing
  `data-testid` hooks to the Shared.Ui components (the existing pages — e.g. `Household.razor` —
  have none yet). Selectors use `data-testid` only, per `tests/E2E.Tests/README.md`.
- Multi-user scenarios use separate Playwright browser contexts (owner + member), each signing in
  via email OTP read from Mailpit — same pattern as `AuthFlowTests`.
- Each test run creates fresh users/households (unique emails) so tests don't depend on reset
  state, matching the existing suite.
- Update the coverage table in `tests/E2E.Tests/README.md` and map each journey to its
  `docs/QA_TEST_PLAN.md` IDs as part of the slice.

---

### E2E-1 — RBAC roster journey

**Status: ✅ Implemented** (`test/e2e-1-roster-journey`). `RosterJourneyTests` (4 tests) +
`Pages/HouseholdPage.cs`/`Pages/JoinPage.cs`; `data-testid` hooks added to `Household.razor` +
`Join.razor` (role assertions use a `data-role` attribute, not localized badge text). Two fixes the
failing tests drove out: the Mailpit OTP poll now matches on the OTP subject (a late outbox-delivered
invitation email to the same address could satisfy the 6-digit regex), and the README documents the
`Auth:RateLimit:PasswordlessPermitLimit` override for local runs (multi-user journeys trip the
5/min/IP production default; CI already set it). Maps to QA-INV-01/02, QA-HH-02/03/09/10/11/12.

**As a** household owner
**I want** the roster UI (invite → join → promote/demote → remove) verified in a real browser
**So that** permission-sensitive UI regressions are caught before they ship

**Context / notes:** Drives `Household.razor` end-to-end. The client-side permission mirror
(`CanManageRoles` = owner only, `CanManageMembers` = owner+admin — ADR-009) shows/hides controls;
the API is the real gate (already integration-tested in `Rbac/*` + `RbacForbiddenIntegrationTests`),
so the E2E asserts the **UI reflects the role**, not the 403s. Invitation email arrives in Mailpit;
the member joins via the `/join` page with the token. New test file
`tests/E2E.Tests/RosterJourneyTests.cs` + `Pages/HouseholdPage.cs`, `Pages/JoinPage.cs`.

**Acceptance criteria**

```gherkin
Scenario: Owner invites a member who joins via the emailed token
  Given a signed-in owner on the Household page
  When the owner invites member@example.test
  Then the pending invitation appears in the list
  And a second browser context signs in as member@example.test and joins via /join with the token
  And the owner's reloaded roster shows the new member with the Member badge

Scenario: Owner promotes and demotes a member
  Given a household with an owner and a member
  When the owner clicks Make admin on the member
  Then the member's badge changes to Admin
  And clicking Make member changes it back

Scenario: The roster is permission-aware for a non-owner
  Given the member (role: member) opens the Household page in their own context
  Then no rename field, invite form, promote/demote, or remove controls are visible
  And after promotion to admin (by the owner), a reload shows rename + invite but NOT promote/demote
  # (No remove buttons appear either: with a two-person roster the only other row is the owner's,
  #  and actions never target the owner or self.)

Scenario: Owner removes a member
  Given a household with an owner and a member
  When the owner removes the member (confirming the dialog)
  Then the member disappears from the roster
```

**Out of scope:** ownership transfer + dissolve (destructive; QA plan covers them manually);
API-level 403 assertions (integration-tested); invitation regenerate/revoke edge cases.
**Definition of done:** tests written first; `data-testid` hooks added to `Household.razor` roster
controls; all scenarios green against the local stack; README coverage table + QA plan mapping
updated; merged, app working.

---

### E2E-2 — Billing seat-quota journey

**Status: ✅ Implemented** (`test/e2e-2-seat-quota-journey`). `SeatQuotaJourneyTests` (2 tests) reusing
`HouseholdPage`; no production changes needed — E2E-1's testid hooks already covered the invite flow.
The shared sign-in helpers (`SignInAsync`/`SignInToHouseholdAsync`/`UniqueEmail`) moved from
`RosterJourneyTests` into `E2ETestBase` for reuse. The seat limit is a named constant in the test file
(`FreePlanSeatLimit = 3`) per the sharp edge below. Maps to QA-HH-14, QA-INV-08.

**As a** household owner on the free plan
**I want** the seat-limit experience verified in a real browser
**So that** the quota → 402 → upgrade-prompt UX (BILLING-5) can't silently regress

**Context / notes:** The free plan's `SeatLimit` is **3** (`src/Core/Billing/PlanCatalog.cs`), and
seats = members + pending invites (a pending invite reserves a seat — `QuotaService`). So a fresh
household (owner = 1 seat) hits the limit after 2 pending invites, entirely from the browser, with
**no Stripe involvement**: the 3rd invite returns 402 `seat_limit_reached` and `Household.razor`
shows the localized `Household_ErrSeatLimit` upgrade message. Revoking an invite frees the seat.
New test file `tests/E2E.Tests/SeatQuotaJourneyTests.cs` (reuses `HouseholdPage` from E2E-1).

**Acceptance criteria**

```gherkin
Scenario: Inviting past the free-plan seat limit shows the upgrade prompt
  Given a fresh household (owner only, free plan)
  When the owner sends invitations to two distinct emails
  Then both appear as pending invitations
  When the owner invites a third email
  Then the seat-limit message is shown ("Upgrade your plan…")
  And no third pending invitation appears

Scenario: Revoking a pending invitation frees the seat
  Given the household is at the seat limit via pending invitations
  When the owner revokes one pending invitation
  Then inviting a new email succeeds and appears as pending
```

**Out of scope:** Checkout/Portal/upgrade flows (no billing UI page exists; the money path is
integration-tested); usage quotas other than seats; trial/dunning banners (no UI surface).
**Definition of done:** tests written first; `data-testid` hooks on the invite form, pending list,
and status alert; scenarios green; README + QA plan mapping updated; merged, app working.

---

### E2E-3 — Notification center & preferences journey

**Status: ✅ Implemented** (`test/e2e-3-notification-journey`). `NotificationJourneyTests` (3 tests) +
prefs locators on `SettingsPage`. The bell already had `notif-bell`/`notif-count`/`notif-panel`
hooks; added `notif-empty` (bell) and `notif-prefs-inapp`/`notif-prefs-email` (prefs card). Maps to
QA-NOTIF-01 (empty state) + QA-NOTIF-03; QA-NOTIF-02 (mark read) stays manual — no browser-triggerable
producer, per the constraint below.

**As a** user
**I want** the notification bell and delivery preferences verified in a real browser
**So that** the NOTIFY UI (bell, prefs card) can't silently break

**Context / notes:** Covers `NotificationBell.razor` (header) and `NotificationPrefsCard.razor`
(Settings). **Constraint (since resolved):** at the time, the only production caller of `NotifyAsync`
was `BillingNotifier` (not browser-triggerable), so "a notification appears and can be marked read"
stayed at the integration layer. **ADMIN-3 later added staff announcements** — a browser-triggerable
producer — and `AnnouncementJourneyTests` now covers the bell list + mark-read end-to-end
(QA-NOTIF-01 full + QA-NOTIF-02). New test file `tests/E2E.Tests/NotificationJourneyTests.cs` +
prefs section on `Pages/SettingsPage.cs`.

**Acceptance criteria**

```gherkin
Scenario: The bell renders with an empty center for a fresh user
  Given a freshly signed-in user
  When they open the notification bell
  Then the dropdown shows the empty state and no unread badge

Scenario: Delivery preferences round-trip
  Given the Settings page notification preferences card (both channels default on)
  When the user turns the email channel off
  And reloads the page
  Then the email toggle is still off and in-app is still on

Scenario: Preferences are per-user
  Given user A turned email off
  When user B (fresh) opens Settings in their own context
  Then user B's channels are both on
```

**Out of scope:** asserting notification creation/mark-read through the browser (no
browser-triggerable producer — integration-tested); realtime/badge-count updates.
**Definition of done:** tests written first; `data-testid` hooks on the bell + prefs card;
scenarios green; README + QA plan mapping updated; merged, app working.

---

### E2E-4 — Magic-link sign-in journey

**Status: ✅ Implemented** (`test/e2e-4-magic-link-journey`). `MagicLinkJourneyTests` (2 tests);
`Mailpit.WaitForMagicLinkAsync` (subject-matched, extracts the verify URL from the HTML) +
`LoginPage.RequestMagicLinkAsync`; testids `login-send-magic-link` + `login-error` added to
`Login.razor`. Maps to QA-AUTH-01, QA-AUTH-05.

**As a** user
**I want** magic-link sign-in verified in a real browser
**So that** the redirect sign-in path (the kind MFA-3 found a bypass in) can't silently regress

**Context / notes:** Added after the epic first closed — reviewing the exclusions showed this was
very automatable: Mailpit already captures the email; the test parses the
`/api/auth/magic-link/verify` URL and opens it (API sets the refresh cookie → `/auth-callback` →
app shell). The unhappy path exercises single-use enforcement in a second, session-less context.

**Acceptance criteria**

```gherkin
Scenario: Magic-link sign-in lands in the app
  Given a user requests a magic link on the login page
  When they open the link from the email
  Then they land signed-in (app shell with sign-out + tenant badge)

Scenario: A used magic link is rejected
  Given a magic link that was already used to sign in
  When a fresh browser context opens the same link
  Then it bounces to /login?error=invalid_link with the error banner and no session
```

**Out of scope:** expiry (clock-dependent — integration-tested), MFA step-up via magic link
(covered by `MfaJourneyTests`' redirect step-up), native (magic link is web-only by design).
**Definition of done:** tests written first; scenarios green; README + QA mapping updated; merged.

---

### E2E-5 — Membership lifecycle (destructive flows) journey

**Status: ✅ Implemented** (`test/e2e-5-membership-lifecycle`). `MembershipLifecycleTests` (4 tests);
testids on the lifecycle card (`transfer-select`/`transfer-submit`/`leave-dissolve`/
`leave-household`) + Settings `delete-account`; `InviteAndJoinAsync` promoted to `E2ETestBase`.
The re-home invariant ("every non-refused leave lands the user in a fresh tenant-of-one",
`TenantService`) is what the assertions pin — the dissolve test renames the doomed household first
to prove the re-homed one is new. Maps to QA-HH-05/07/08, QA-SET-07.

Added with E2E-4: "destructive" was a weak exclusion — every test creates
fresh throwaway users, so these flows are safely automatable. Transfer ownership, member leave,
solo-owner dissolve, and account deletion, all through the real UI with confirm dialogs.

**As a** household owner (or member)
**I want** the membership lifecycle verified in a real browser
**So that** the highest-consequence flows in the app can't silently regress

**Context / notes:** Drives the lifecycle card on `Household.razor` (transfer select + button,
leave/dissolve buttons, `window.confirm` dialogs) and the delete-account flow in Settings (GDPR-2
UI). Assertions stay UI-observable: badges swap after transfer, the leaver lands signed-out or in
a fresh household state, deleted accounts can't sign back into the old household.

**Acceptance criteria**

```gherkin
Scenario: Owner transfers ownership
  Given a household with an owner and a member
  When the owner transfers ownership to the member
  Then the member's badge shows Owner and the ex-owner sees member-level controls

Scenario: Member leaves the household
  Given a member of a two-person household
  When they leave (confirming the dialog)
  Then they are out — and the owner's reloaded roster no longer lists them

Scenario: Solo owner dissolves the household
  Given a sole owner
  When they leave-and-delete (confirming the dialog)
  Then the household is gone and the UI lands in a coherent signed-in-or-out state

Scenario: User deletes their account
  Given a member with no ownership
  When they delete their account from Settings (confirming)
  Then they are signed out and the owner's roster no longer lists them
```

**Out of scope:** dissolve-with-billing (provider cancellation is BILLING-7, integration-tested);
export-before-erasure flows (QA covers).
**Definition of done:** tests written first; testids on the lifecycle card + delete-account
controls; scenarios green; README + QA mapping updated; merged, app working.

---

## Slice plan (implementation map)

Ordered, each a mergeable vertical slice. TDD throughout — the failing Playwright test drives the
`data-testid` additions (the only production-code changes this epic should need).

1. ✅ **RBAC roster journey (E2E-1).** — DONE. Page objects `HouseholdPage`/`JoinPage`, testid hooks
   on `Household.razor`/`Join.razor`, multi-context invite→join→promote→remove journey; Mailpit OTP
   poll hardened (subject match) + rate-limit override documented.
2. ✅ **Seat-quota journey (E2E-2).** — DONE. Reuses `HouseholdPage`; free-plan limit (3) hit via
   pending invites; asserts the 402 upgrade prompt without touching Stripe; sign-in helpers
   promoted to `E2ETestBase`.
3. ✅ **Notification journey (E2E-3).** — DONE. Bell empty state + prefs persistence, per-user
   isolation; testids on the bell empty state + prefs toggles.
4. ✅ **Magic-link journey (E2E-4).** — DONE. Happy path + single-use rejection;
   `Mailpit.WaitForMagicLinkAsync`; testids on the send button + login error banner.
5. ✅ **Membership lifecycle journey (E2E-5).** — DONE. Transfer, leave, dissolve, delete account —
   all with fresh throwaway users; testids on the lifecycle card + Settings delete-account button;
   assertions pin the re-home-to-fresh-tenant invariant.

**Known sharp edges:**
- **Selectors are `data-testid`-only** — the touched components don't have hooks yet; adding them
  is part of each slice, not a separate refactor.
- **Seats count pending invites** — E2E-2 depends on that `QuotaService` rule; if the seat rule
  changes, this journey is the canary.
- **Free-plan `SeatLimit: 3` is an EXAMPLE quota** (`PlanCatalog.cs` says "tune per app") — the
  test should read failure gracefully: if a downstream app retunes the catalog, the test's invite
  count must follow. Keep the limit referenced in one constant in the test file.
- **No notification producer is browser-reachable** — don't be tempted to add a test-only endpoint
  to fake one; the integration tests own that behavior.
- **English-locale assertions only** — the suite runs in EN (matching `I18nTests`' approach of
  testing the switcher separately); don't assert localized strings in journey tests, use testids.
