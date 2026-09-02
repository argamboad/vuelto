# Epic PREFS — per-user preferences that actually follow the user

> Epic key: `PREFS`. Builds on THEME-1 (`theme.md`) and the locale playbook (B7-3/MITI-5).
> Fixes the QA-I18N-02 failure (locale never persisted server-side from the UI) and the
> "theme applies only after a manual reload" instability. ADR-022.

### PREFS-1 — Locale and theme follow the user across devices

**As a** signed-in user
**I want** my language and theme choices saved to my account and applied on every device I sign in on
**So that** the app looks and reads the same everywhere without re-picking my preferences

**Context / notes:**
- Storage stays as-is (ADR-C2 carve-out): `User.Locale` + `User.Theme` columns, `PUT
  /api/auth/locale` / `PUT /api/auth/theme`, claims on the JWT. No schema change.
- Pre-slice defects this story closes:
  1. The only `LanguageSwitcher` lived on the login page, where the user is always anonymous —
     `PUT /api/auth/locale` was unreachable from the UI, so `User.Locale` was always null.
  2. The server→device reconcile ran only in `MainLayout.OnInitializedAsync` (cold start), so
     OTP/MFA sign-ins (soft navigations) didn't apply the saved preferences until a manual reload
     (`ThemeJourneyTests` had a workaround `ReloadAsync` proving it).
  3. The B7-3 in-process culture switch doesn't load WASM satellite resource assemblies, so even
     when the reconcile ran, strings could stay English until the next reload.
  4. Theme "system" was stored as null, indistinguishable from "never chose", so switching back
     to System on one device never propagated to others.
- Design (ADR-022): a `/settings` Preferences card gives the switchers a signed-in home;
  `AuthService` raises a `SignedIn` event so `MainLayout` reconciles on every sign-in path; on a
  locale mismatch the reconcile persists + full-reloads once (satellite-assembly-safe — reload
  happens only on an actual mismatch, so B7-3's "guaranteed double reload" stays fixed); theme
  "system" is stored explicitly; and a device preference with no server counterpart is adopted
  server-side on sign-in (so a pre-auth login-page choice becomes the account preference).

**Acceptance criteria**

Scenario: signed-in user changes language from Settings
  Given I am signed in
  When I open /settings and choose Español in the Preferences card
  Then the UI re-renders in Spanish
  And my user record's locale is "es" (PUT /api/auth/locale)

Scenario: locale follows the user to a fresh browser
  Given my saved locale is "es"
  When I sign in on a browser that has never seen my locale
  Then the app renders in Spanish without me touching the switcher
  And the device store now caches "es" for the next cold start

Scenario: pre-auth device choice is adopted on sign-in
  Given I chose Español on the login page (anonymous, device-local only)
  And my user record has no saved locale
  When I sign in
  Then my user record's locale becomes "es"

Scenario: theme applies immediately after an OTP sign-in (no manual reload)
  Given my saved theme is "dark"
  When I sign in with an email code on a fresh browser
  Then the page switches to dark as part of signing in
  And no manual reload is needed

Scenario: choosing System is a real preference that propagates
  Given my saved theme is "dark" and a second browser shows dark
  When I switch my theme to System on the first browser
  Then my user record stores "system" (not null)
  And the second browser returns to the OS scheme on its next sign-in/reload reconcile

Scenario: impersonation never rewrites the impersonated user's preferences
  Given I am platform staff impersonating a user
  Then no preference adoption PUT is issued on my behalf

**Out of scope:** notification preferences (NOTIFY-2 owns them); a generic preferences
table/endpoint (declined — see the 2026-07-14 discussion; storage stays per-column); native
(MAUI) UI changes beyond what the shared RCL provides automatically.

**Definition of done:** tests written first (TDD); Api.Tests cover the "system"-is-stored rule;
E2E covers the cross-browser locale journey and the reconcile-on-sign-in theme journey (workaround
reload removed); QA_TEST_PLAN updated in the same PR (R31) + PDFs regenerated; ADR-022 added;
Postman descriptions updated; app working.
