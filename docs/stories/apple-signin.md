# Stories — Apple Sign In

> One file per epic. Adds Apple as a third OAuth provider on the existing provider-agnostic auth
> stack (ADR-002). **Status: DEFERRED** — design decision and constraints in **ADR-005**; pick this
> up web-first when there's a business need. Stories use Gherkin acceptance criteria.

**Epic key:** `APPLE`

**Prerequisites (external, before any code):**
- Apple Developer Program enrollment ($99/yr).
- Apple portal setup: **App ID**, a **Service ID** (this is the OAuth `client_id`), a **Sign in with
  Apple key** (`.p8`, note Key ID + Team ID), and a **verified return domain** (Apple rejects
  `localhost` — see ADR-005 #3).
- A **public HTTPS dev domain or tunnel** (e.g. Cloudflare Tunnel/ngrok) pointed at the API, because
  the Apple callback can't be `localhost`.
- Confirm a **stable** `AspNet.Security.OAuth.Apple` build targets .NET 10 (no previews — ADR-C10).

---

### APPLE-1 — Sign in with Apple on the web (happy path + first-time onboarding)

**As a** prospective or returning user on the web login page
**I want** to authenticate with my Apple ID
**So that** I can use Apple as a sign-in method like Google or Microsoft

**Context / notes:** mirrors the Google flow at [Login.razor:44](../../src/Shared.Ui/Pages/Login.razor)
and the callback at [AuthController.cs:71](../../src/Api/Controllers/AuthController.cs). First-ever
sign-in must auto-create a household and make the user owner (same as QA-ONB-01 in `QA_TEST_PLAN.md`).
Apple asserts `email_verified`, so the same-email auto-link path (ADR MITI-3 / `ProviderEmailTrust`)
applies; private-relay addresses (`@privaterelay.appleid.com`) are valid verified emails.

**Acceptance criteria**

```gherkin
Scenario: First-time Apple sign-in creates an account and household
  Given I am an anonymous user on /login
  And no account exists for my Apple email
  When I click "Continue with Apple" and complete Apple consent (sharing my email)
  Then I am returned to the app signed in, on the home page
  And a new household is created with me as owner
  And the header shows that household and my name

Scenario: Returning Apple user signs back into the same account
  Given I previously signed in with Apple
  When I sign in with Apple again
  Then I land in the same account and household (no duplicate account, no display-name loss)

Scenario: Apple email matches an existing verified account — auto-link, no duplicate
  Given an account already exists for my email (created via Google/Microsoft/email)
  When I sign in with Apple using that same Apple-verified email
  Then the Apple login is linked to the existing account
  And I am signed into that existing account and household

Scenario: User denies consent at Apple
  Given I started the Apple flow from /login
  When I cancel or deny consent at Apple
  Then I am returned to the client auth-error page, not a 500
```

**Out of scope:** linking/unlinking from Settings (APPLE-2); native desktop/Android (APPLE-3);
Apple private-email-relay forwarding configuration (an Apple-account concern, not app code).
**Definition of done:** tests written first (TDD); all unit + E2E scenarios green; `ClaimsExtractor`
mapping and `ProviderEmailTrust` arm unit-tested; E2E covers happy + denied-consent paths against the
public HTTPS dev domain; tenant-scoping verified; merged, app working; ADR-005 referenced.

---

### APPLE-2 — Link / unlink Apple from Settings

**As a** signed-in user
**I want** to link or unlink my Apple ID from the linked-accounts screen
**So that** I can manage Apple as one of my sign-in methods

**Context / notes:** mirrors the provider link/unlink rows in
[Settings.razor](../../src/Shared.Ui/Pages/Settings.razor) and the link-token path at
[AuthController.cs:293](../../src/Api/Controllers/AuthController.cs). Reuses the existing
"already in use" guard (QA-SET-03) and the fail-closed unlink confirm (QA-SET-04). Unlinking the only
provider must never lock the user out — email sign-in remains (QA-SET-05).

**Acceptance criteria**

```gherkin
Scenario: Link Apple to an existing account
  Given I am signed in and Apple is not linked
  When I click "Link" on Apple and complete consent with an Apple ID not used by another account
  Then Apple shows as Connected and I can subsequently sign in with it into the same account

Scenario: Linking an Apple identity already owned by another account is rejected
  Given an Apple identity is already linked to a different user
  When I try to link it to my account
  Then I see an "already in use" error and it is not linked

Scenario: Unlink Apple
  Given Apple and at least one other sign-in method are on my account
  When I click "Unlink" on Apple and confirm the dialog
  Then the row returns to a "Link" state
```

**Out of scope:** the sign-in flow itself (APPLE-1); native (APPLE-3).
**Definition of done:** tests written first; unit + E2E scenarios green; "already in use" and
fail-closed-confirm paths covered; tenant-scoping verified; merged, app working.

---

### APPLE-3 — Apple on native (MAUI desktop/Android) — DEFERRED

**As a** desktop/Android user
**I want** to sign in with Apple in the native shells
**So that** Apple parity extends beyond web

**Context / notes:** explicitly deferred (ADR-005 #6, ADR-C9 web-first). No native Apple SDK on
Windows/Android, so this reuses the web flow through the loopback/`perezosoft://` path and inherits
Apple's no-`localhost` (#3) and `form_post` (#4) constraints — a materially larger effort than the
web slice. Do **not** start this before APPLE-1 ships and is validated on web.

**Acceptance criteria:** _to be written when this epic is undeferred._
**Out of scope:** everything until APPLE-1/APPLE-2 are merged.
**Definition of done:** n/a while deferred.

---

## Slice plan (implementation map — when undeferred)

Ordered, each a mergeable vertical slice. TDD throughout (write the failing test first).

1. **Provider wiring (APPLE-1 backbone).**
   - Add `AspNet.Security.OAuth.Apple` (verify stable .NET 10 build).
   - `ServiceCollectionExtensions.AddInfrastructure`: add an `.AddApple(...)` block guarded by config
     presence, `SignInScheme = ExternalScheme`, `Events.OnRemoteFailure = OnRemoteFailure`. Configure
     the ES256 client-secret generator from `Authentication:Apple:{ServiceId,TeamId,KeyId,PrivateKey}`.
     Set the correlation cookie to `SameSite=None; Secure` for the `form_post` callback.
   - `AuthProviders`: add `Apple` const, add to `Supported`, add the `SchemeFor` arm
     (`AppleAuthenticationDefaults.AuthenticationScheme`).
   - `ProviderEmailTrust`: add `AuthProviders.Apple => true`.
   - **Tests first:** `AuthProvidersTests` (supported + scheme), `ProviderEmailTrustTests` (Apple arm),
     `ClaimsExtractorTests` (Apple principal → email/sub/email_verified) — confirm no extractor change
     is needed, lock it with a test.
2. **Config + docs.**
   - `.env.example`: document `Authentication__Apple__ServiceId/TeamId/KeyId/PrivateKey` (placeholders;
     private key is multi-line — note the loading approach). Keep `.env` gitignored (ADR-001).
   - Update `QA_TEST_PLAN.md` (§5 + traceability matrix) with QA-AUTH cases for Apple and a **note that
     Apple QA requires the public HTTPS domain**, not localhost. Update `FEATURES.md` if it enumerates
     providers.
3. **Web UI (APPLE-1).** Add the "Continue with Apple" button + Apple SVG glyph + `.btn-apple` style in
   `Login.razor` (both web-anchor and native-button variants), wired to `/api/auth/login/Apple`.
4. **Settings link/unlink (APPLE-2).** Add the Apple row to `Settings.razor`; reuse existing
   link-token + in-use guard + fail-closed unlink confirm.
5. **E2E.** New `AppleAuthTests` mirroring `AuthFlowTests`, run against the public HTTPS dev domain
   (document the override in `tests/E2E.Tests/README.md`).

**Known sharp edges (from ADR-005):** rotating ES256 secret (not static); no-`localhost` redirect;
`form_post` correlation cookie; display name only on first auth. Budget for Apple-portal setup and a
tunnel, not just code.
