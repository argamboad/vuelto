# Conversion side by side (Pkg V.5, #226) — 2026-11-06

vuelto at the same app content, two ways: the **baseline** is `develop` at `732abe3` (the platform's code in the repo,
stamp `e7a2962`); the **converted** tree is `packaging/conversion` at `65c59ae` (the platform's packages,
`1.0.0-beta.10`), whose app content is that same commit's. Done when nothing differs. Nothing does, except what the
packages move on purpose, each listed below with where it went. Same recipe as jigger-jot's pilot
(`J.7`, jigger-jot `docs/qa-runs/2026-10-pilot-side-by-side.md`).

## Both suites

| | Baseline | Converted |
|---|---|---|
| `Core.Tests` | 436 ✅ | — (vuelto's 25 classes joined `Api.Tests/App/` in V.3) |
| `Api.Tests` | 1520 ✅ | 868 ✅ |
| `Ui.Tests` | 456 ✅ | 404 ✅ |
| `ui-js` | — (inside the platform's js tests) | 6 ✅ |

The difference is the platform's own tests, which run in perezosoft-platform now. Compared by `Class.Method` from
`dotnet test --list-tests` (a theory counts once):

- **123 classes in both**, every vuelto class among them; 119 with the same tests.
- **Baseline-only: 154 classes, 882 tests**, every one found in perezosoft-platform's suites. Three were renamed on the
  way: `TenantInvariantTests`, `TenantScopeFilterTests` and `TenantStampingInterceptorTests` are the packaged
  `TenancyTests` (Pkg 5.1b).
- **Four shared classes are smaller in the converted tree: 35 names**, all in the platform's suites: four
  `ArchitectureTests` facts about the platform's own tree, two `NativeChromeGateTests` and two `NativeShellGateTests`
  facts about files the packages or the platform's docs own now (theme.js, the publish script, REBRANDING, the first-release
  notes; V.3/V.4), and 27 `SessionKeepAliveTests` cases that are the platform's `Client.Tests`.
- **Converted-only: 3 classes, 26 tests**, the inherited suites from `Perezosoft.Platform.Testing`:
  `CompositionSmokeTests`, `OwnershipTests`, `TenancyTests`.

## The schema, on a copy of staging

Two Neon branches of the staging database (project "Y el vuelto?", parent `production` at LSN `0/4C0AC08`):
`v5-baseline` for the baseline, `v5-pkg` for the converted tree. Staging's history ends at
`20261009180331_AddCardPatterns` (47 rows): 2 households, 354 transactions, 395 staged vouchers. On both branches, before
any host, the two connected inboxes were set to `needs_reconsent` (the same statement on each), so the mail poller could
not read the owner's real mailboxes from a copy. Each API host started on its own branch as staging starts it
(`Production`, migrations on start), mail pinned to the local Mailpit, billing off, then stopped at ready.

| | Baseline host | Converted host |
|---|---|---|
| Migrations applied | none ("already up to date") | `20261106170359_UserMfaFailedAttemptCountDefault` (`ALTER … SET DEFAULT 0`) |
| `/health/ready` | 200 | 200 |
| `/api/version` | `platform: e7a2962…` | `platform: 1.0.0-beta.10` |

- `pg_dump --schema-only`: **identical**, before either host and after both. The catch-up migration restates a default
  the history had already set.
- `pg_dump --data-only`: **identical** but for the converted host's one `__EFMigrationsHistory` row. The baseline host
  changed no row at all.

## E2E and Postman against both API hosts

The same tests against both stacks: the baseline's `E2E.Tests` build (the platform's journeys and vuelto's, 41 + 4) and
its Postman collection. Each stack got a fresh database on vuelto's compose Postgres and its Mailpit, wired as the e2e
tool wires it (the converted stack through `dotnet platform`'s e2e script, pointed at the baseline's test build).

| | Baseline stack | Converted stack |
|---|---|---|
| E2E, main lane (billing on) | 41/41 ✅ | 41/41 ✅ |
| E2E, gates lane (billing off, green list) | 4/4 ✅ | 4/4 ✅ |
| Postman: requests / assertions / failed | 161 / 198 / 12 | 161 / 198 / 12 |

- **Postman results are identical request by request**: same sequence, same status code, same pass or fail on every
  assertion. All 117 JSON bodies have the same shape (keys and types; ids and times vary).
- The 12 Postman failures are the same on both: requests that need a value a person types or configures (a TOTP code,
  a staff address, a public-API key), an export token past its lifetime after the pauses, the report's two assertions
  that need a month with spending, and the mail-consent callback, whose redirect goes to a web client this run did not
  start. The run used `newman@6` with a 7 s pause between requests, so the Mailpit fetch reads the code the outbox has
  just sent. It used a copy of the collection without `Logout`, `⚠ Delete my account` and `MFA — verify (step-up)`: the
  collection is built for clicking through, and a straight run signs itself out and then deletes its own account.

## Android

- `dotnet build src/Maui -f net10.0-android`: **0 errors on both** (the one warning is `adb reverse` with no emulator
  attached).
- **The device leg** (CI's manual *Run workflow*, `devices`) needs the branch on GitHub; it runs when the conversion is
  pushed (V.6). vuelto is public, so its minutes are free (~30).

## Noted

- Running the baseline's E2E against the converted stack is the stronger check: the converted E2E project keeps only
  the starter's smokes and vuelto's journeys (15, V.3). The platform's journeys run in perezosoft-platform.
- The branches `v5-baseline` and `v5-pkg` stay until the owner says to delete them, with V.2's `rehearsal-v2-catchup`
  and the snapshot `before-v2-catchup-2026-11-06`.
