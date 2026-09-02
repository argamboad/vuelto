# Localization (i18n)

The app is localized with standard .NET resources (`.resx` + `IStringLocalizer`), shared
across web/desktop/mobile through the RCL.

**Shipped languages:** English (`en`, the neutral/fallback) and Spanish (`es`).
`fr` / `de` / `pt` are pre-cleared in the API's accepted set and the runtime supports them,
but their translations are **not done yet** — see "Adding a language" below.

## Where the strings live
| Surface | Resource files |
|---|---|
| UI (all razor pages/components) | `src/Shared.Ui/Resources/AppStrings.resx` (en) + `AppStrings.{culture}.resx` |
| Transactional emails | `src/Infrastructure/Email/EmailStrings.resx` (en) + `EmailStrings.{culture}.resx` |

**Key naming — namespace per feature** (v3 audit T58): `AppStrings.resx` is one shared file, so a
slice's keys carry the feature prefix — `Notes_Title`, `Notes_Empty`, `Billing_Upgrade` — never bare
`Title`/`Empty`, which collide across slices. Platform surfaces already follow this
(`Notif_*`, `Join_*`, `Plan_*`/`BillingStatus_*`); copy the convention (add-a-slice checklist step 8
in `WAYS_OF_WORKING.md`).

Any key missing from a culture file falls back to the neutral (English) file.

## How the language is chosen
- **Signed-in users:** a per-user `User.Locale` (saved via `PUT /api/auth/locale` from the
  **Settings → Preferences** card, carried in the JWT `locale` claim) — it follows the user
  across devices. `MainLayout` reconciles to it on **every sign-in** (cold start + the
  `AuthService.SignedIn` event): on a mismatch it persists the saved locale to the device and
  full-reloads **once** so the WASM satellite resource assemblies actually load; when the user
  never chose a locale, an explicit device-local choice is adopted server-side instead
  (PREFS-1, ADR-022).
- **Anonymous / pre-login:** a device-local store read at startup, written by the in-app
  `LanguageSwitcher` (login card + Settings) through the `ICulturePersistence` seam:
  `localStorage["app_culture"]` on web (read pre-render in `Web/Program.cs`) and OS
  `Preferences["app_culture"]` on MAUI (read in `MauiProgram` — NATIVE-5), falling back to
  English (web) / the OS culture (MAUI). A pre-login pick becomes the account preference on
  sign-in (adoption above).
- **Emails:** OTP / magic-link use the requester's current UI culture (the Login page sends
  it); invitations use the **inviter's** saved locale.

The WASM host loads full globalization data (`BlazorWebAssemblyLoadAllGlobalizationData`) so
non-English cultures format dates/numbers correctly.

## Adding a language (example: French, `fr`)
1. Copy `src/Shared.Ui/Resources/AppStrings.resx` → `AppStrings.fr.resx` and translate every
   `<value>`.
2. Copy `src/Infrastructure/Email/EmailStrings.resx` → `EmailStrings.fr.resx` and translate.
3. Add it to the dropdown: append `("fr", "Français")` to `Cultures` in
   `src/Shared.Ui/Components/LanguageSwitcher.razor`.
4. Confirm the code is in `SupportedLocales` in `src/Api/Controllers/AccountController.cs`
   (already includes `en, es, fr, de, pt`).

That's all — the DB column, the `/api/auth/locale` endpoint, and globalization data already
accept any of the five. No other code changes.

> ⚠️ **Translate the email + auth copy with a native speaker before release.** Machine
> translation is fine as a starting point, but this microcopy carries nuance.

## Rebranding note
The `.resx` files contain the brand name ("Perezosoft") and product copy in **each language**
— they're part of the rebranding checklist (`docs/REBRANDING.md`), not just the razor markup.
