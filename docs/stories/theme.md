# Stories — Dark mode (`theme` preference)

> One file per epic. Adds a per-user **Light / Dark / System** theme on Bootstrap 5.3's
> `data-bs-theme`, following the two proven preference playbooks: device-local persistence for
> anonymous/cold-start (the NATIVE-5 culture seam) and a server-side per-user column that follows
> the user across devices (the `User.Locale` playbook). Platform chassis work — no
> `src/Api/Features/**` code. **Status: ✅ COMPLETE** — THEME-1 merged.

**Epic key:** `THEME`

**Prerequisites (external, before any code):** none — Bootstrap 5.3 (bundled) ships
`data-bs-theme`; the dark brand lockups already exist (`lockup_dark.svg` / `lockup_dark_1520.png`).

---

### THEME-1 — Per-user dark mode (switcher + device persistence + server sync)

**Status: ✅ Implemented** (`feat/theme-dark-mode`). `theme.js` pre-paint bootstrap (both hosts'
`index.html`, kept in parity); `IThemePersistence`/`LocalStorageThemePersistence` (RCL — one impl
serves web + MAUI, the WebView owns the localStorage); `ThemeSwitcher.razor` in the header +
login page; `User.Theme` + `AddUserTheme` migration + `PUT /api/auth/theme` + `theme` JWT claim;
`MainLayout` cold-start reconcile; dark token block in `app.css` + scoped-CSS fixes; EN/ES
strings. Tests: `UserServiceTests` (set/overwrite/clear, unknown-user no-op),
`JwtTokenServiceTests` claim round-trip, `ThemeJourneyTests` E2E (suite 30→31).

**As a** signed-in user (or a visitor on the login page)
**I want** to choose Light, Dark, or follow my OS scheme — and have the choice stick everywhere
**So that** the app is comfortable to use in dark environments on every device I sign in to

**Context / notes:** ~~"System" is the default and is stored server-side as **null**~~
**Amended by PREFS-1 (ADR-022, 2026-07-14):** `"system"` is now stored **verbatim** on
`User.Theme` like the other two values, so switching back to System propagates across devices;
null means "never chose" and lets sign-in adopt a device-local choice. The reconcile also runs
on **every sign-in** (the `AuthService.SignedIn` event), not just cold starts — see
`docs/stories/prefs.md`. (`User` is not `ITenantScoped`, so no RLS policy is involved.) The
claim lands in the JWT on the next refresh. The pre-paint bootstrap reads
`localStorage["app_theme"]` inside the (web)view, so no native `Preferences` store is needed
(unlike culture, nothing must be read C#-side before the WebView exists).

**Acceptance criteria**

```gherkin
Scenario: Flip to dark, live
  Given I am signed in
  When I pick "Dark" in the header theme switcher
  Then the page restyles immediately without a reload
  And <html> carries data-bs-theme="dark"

Scenario: Choice survives a cold start with no flash
  Given I chose "Dark" earlier on this device
  When I reload or reopen the app
  Then the first paint is already dark (pre-paint bootstrap, no light flash)

Scenario: System follows the OS
  Given my theme is "System" (the default)
  When my OS switches between light and dark
  Then the app follows live

Scenario: The choice follows the user across devices
  Given I chose "Dark" on device A while signed in
  When I sign in on device B and the app next cold-starts
  Then device B renders dark (server-stored preference reconciled by the layout)

Scenario: Unsupported theme value
  Given I am signed in
  When a client PUTs /api/auth/theme with a value outside light|dark|system
  Then the API responds 400 "unsupported_theme" and nothing is stored
```

**Out of scope:** themed transactional email (email HTML can't follow the client scheme);
per-tenant theme defaults; additional palettes beyond light/dark.
**Definition of done:** tests first (service, claim, E2E); Postman "Set theme" entry;
DATA_MODEL/FEATURES/QA plan updated in the same PR; both `index.html` files in sync; merged,
app working.
