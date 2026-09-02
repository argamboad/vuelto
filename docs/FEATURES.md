# Features & User Flows

> How the product behaves, flow by flow. The "why/what" lives in `PROJECT_BRIEF.md`; structures
> live in `DATA_MODEL.md`. Fill one section per major flow. Pattern shown below.

## Flow template (copy per flow)

### N. <Flow name>
**Goal:** <what the user is trying to accomplish>

Flow:
1. <step>
2. <step>

Notes: <edge cases, derived-rule references, tenant-scoping considerations>

---

## Constant flows (auth + tenant onboarding — always present)

> These reflect the platform's **custom JWT + refresh-token** implementation (no ASP.NET Core
> Identity). Endpoint names match `AuthController` / `HouseholdInvitationsController`. The
> step-by-step QA scripts live in `docs/QA_TEST_PLAN.md`.

### 1. Sign in via OAuth (Google / Microsoft / future providers)
**Goal:** a user authenticates using their existing identity provider account.

Flow:
1. User clicks "Continue with Google" (or Microsoft) on the login page.
2. Browser navigates to `GET /api/auth/login/{provider}`; the API challenges the provider.
3. Provider redirects to `GET /api/auth/callback/{provider}`; the external principal rides a
   temporary `External` cookie scheme.
4. `UserService.GetOrCreateUserAsync` resolves the account: known `UserLogin` → that user; else a
   matching **verified** email → links the new provider (an **unverified** email match is refused —
   the takeover guard); else a brand-new `User` + fresh `Tenant` + owner `TenantMembership` are
   created atomically.
5. The API sets the refresh-token cookie and redirects to the client, which calls
   `POST /api/auth/refresh` to obtain its JWT access token.

Notes:
- Adding a provider = one `.AddXxx()` in `ServiceCollectionExtensions` + provider registration.
- A user can link multiple providers (rows in `UserLogin`); there is no `AspNetUserLogins`.
- **MFA step-up (ADR-012):** if the resolved user has MFA enabled, primary auth does **not** issue a
  full session — it returns a short-lived signed **challenge** and the callback redirects to
  `/login?mfa=<challenge>`; the client completes step-up via `POST /api/auth/mfa/verify` (a TOTP or
  recovery code). See §6 for enroll/manage.

### 2. Sign in via magic link (web, passwordless)
**Goal:** a user signs in without a password by clicking an emailed link.

Flow:
1. User requests a link: `POST /api/auth/magic-link/send` (always 200 — no account is created yet,
   so the response can't be used to probe for accounts).
2. `PasswordlessService` stores a single-use, hashed `LoginToken` (`purpose = magic-link`, 15 min
   default) and emails the URL via `IEmailSender` (Mailpit in dev).
3. User clicks it → `GET /api/auth/magic-link/verify`; the token is validated and **consumed**
   (`consumed_at`). The account is resolved/created now (`GetOrCreateByEmailAsync`, marked
   email-verified), provisioning a tenant if new.
4. The API sets the refresh cookie and the client refreshes into its session.

Notes:
- Only the token **hash** is stored; lifetime is `Auth:MagicLink:TokenLifespanMinutes`.
- Single-use: a redeemed or expired link no longer works.
- **MFA step-up (ADR-012):** an MFA-enabled user is redirected to `/login?mfa=<challenge>` instead of
  a session; the client completes it via `POST /api/auth/mfa/verify`.

### 3. Email OTP sign-in (web + native)
**Goal:** authenticate with a one-time 6-digit code — the only passwordless method on native
clients, where a magic-link email can't return to the app.

Flow:
1. User requests a code: `POST /api/auth/otp/send` (always 200). A hashed `LoginToken`
   (`purpose = otp`, 6 digits, 10 min default) is stored.
2. User enters the code: `POST /api/auth/otp/verify`. On match it's consumed and the session is
   issued; wrong codes increment `attempt_count` and lock out after the max (default 5).

Notes:
- **MFA step-up (ADR-012):** on a correct OTP, if the user has MFA enabled the API returns an
  `{ mfa_required, challenge }` response (JSON path) rather than a session; the client completes it via
  `POST /api/auth/mfa/verify` with a TOTP or recovery code. Enforced on **every** sign-in path.
- Email OTP here is the passwordless *primary* factor; authenticator-app **TOTP** is the optional
  *second* factor (enroll/manage in §6). SMS OTP is deferred (needs a phone field + an SMS provider).

### 4. New-tenant onboarding (automatic)
**Goal:** a newly authenticated user lands in their own tenant with no extra step.

Flow:
1. On first sign-in (any method) `UserService` creates the `User`, a fresh `Tenant`
   ("<name>'s Household"), and an owner `TenantMembership` in one transaction.
2. There is **no separate "create household" screen** and **no `tenant_id` on User** — tenancy is
   the membership. The user can rename the household later on `/household`.

### 5. Invite a member to the household
**Goal:** an owner **or admin** invites someone to their tenant.

Flow:
1. An owner or admin submits an email: `POST /api/household/invitations` (gated by
   `Permission.ManageMembers`, which both owner and admin hold — RBAC, ADR-009). A `TenantInvitation`
   is created (status `pending`, hashed token); inviting an existing member is refused (409), a pending
   invite for the same email is refreshed, not duplicated, and hitting the plan's seat cap returns
   **402 `seat_limit_reached`** (BILLING-5 — pending invites reserve a seat).
2. The raw token is returned once (revealed in the UI) **and** emailed as `/join?token=...`.
3. The invitee opens `/join`, signs in if needed, then `POST /api/household/invitations/accept`
   validates the token, moves their `TenantMembership` to the inviting tenant, and consumes the
   invite. A departing solo owner's empty tenant is dissolved (the re-home invariant).

Notes:
- `TenantInvitation.is_valid` = `status == pending AND !is_expired` (derived, not stored).
- An owner or admin can regenerate (new token; the old one dies) or revoke a pending invite.
- A user is always in exactly one tenant — accepting **moves** them, never adds a second.

### 6. Account settings (linked providers + language + theme)
**Goal:** manage per-user account settings on `/settings`.

- **Linked accounts:** `GET /api/auth/logins` lists linked providers;
  `POST /api/auth/link/{provider}` links another (refused if that identity belongs to someone else);
  `DELETE /api/auth/logins/{provider}` unlinks (email sign-in always remains, so this can't lock
  you out).
- **Language:** the switcher (Settings → Preferences card; also on the login page for pre-auth
  picks) persists the user's locale via `PUT /api/auth/locale`; it lands in the JWT on the next
  refresh and localizes the UI and outgoing emails. See `docs/LOCALIZATION.md`.
- **Theme (THEME-1 + PREFS-1):** a Light/Dark/System switcher in the header, Settings →
  Preferences, and the login page. Applies live via Bootstrap's `data-bs-theme`; persists
  device-locally (`localStorage["app_theme"]`, applied pre-paint by `theme.js`) and — signed in —
  server-side via `PUT /api/auth/theme` ("system" stored verbatim — ADR-022). See
  `docs/stories/theme.md`.
- **Preference sync (PREFS-1, ADR-022):** both preferences follow the *user*: reconciled on every
  sign-in (server value wins — theme applies live, a locale mismatch reloads once), and a
  device-local choice made before signing in is adopted into the user record when the account has
  none. See `docs/stories/prefs.md`.
- **MFA (authenticator TOTP; ADR-012):** enroll via `POST /api/auth/mfa/enroll` (returns an
  `otpauth://…` provisioning URI to render as a QR + one-time recovery codes), confirm possession with
  a valid code to enable, and disable/regenerate recovery codes from Settings. Once enabled, every
  sign-in path (§§1–3) requires the step-up (`POST /api/auth/mfa/verify`). The secret is encrypted at
  rest and never returned after enrollment; recovery codes are hashed + single-use, and are short,
  human-typeable `xxxxx-xxxxx` codes (unambiguous alphabet; entry is case/hyphen/space-insensitive).

---

## App-specific flows
<!-- The heart of the product. One subsection per flow, using the template above. -->
_TODO_

## Flow-to-rule cross-reference
| Flow | Key derived rule |
|------|------------------|
| Invite member (§5) | `TenantInvitation.IsValid` (`status == pending && !is_expired`) |
| Magic link / OTP sign-in (§2, §3) | `LoginToken` single-use (`consumed_at`) + expiry |
| _TODO_ | _TODO_ |

## Out of scope
- SMS OTP — deferred until phone-based OTP is needed (no phone field / SMS provider yet).
- Social login beyond Google + Microsoft — infrastructure is provider-agnostic; add per-app.
- See the OUT list in `PROJECT_BRIEF.md`.

_(Authenticator-app **TOTP MFA** is **implemented** — ADR-012, §6 + the sign-in step-up in §§1–3.)_
