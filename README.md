# ¿Y el vuelto?

Personal finance for Costa Rican **households that live in two currencies** (₡ CRC + $ USD).
Every expense is captured in both currencies at that day's rate, budget months follow the
household's **pay cycle** (weeks anchored on a chosen weekday, not the calendar), and bank voucher
emails from BAC/BN become transactions after a one-click review.

The name is Costa Rican slang — *"¿Y el vuelto?"*, "and the change?". The code, database and
docs keep the name **`Vuelto`**; the display brand is *¿Y el vuelto?* — that split is intentional.

Built on the [Perezosoft platform](https://github.com/argamboad/perezosoft-platform), taken as **packages**
(`Perezosoft.Platform.*` from its GitHub Packages feed, ADR-034): ASP.NET Core API + Blazor WASM + shared RCL +
PostgreSQL/EF Core + custom JWT auth, with MAUI Blazor Hybrid shells. Multi-tenancy, auth, invitations, MFA, billing,
jobs, notifications, GDPR and the CI gates come from the packages; this repo is the host (`src/Host`: the budgeting
domain as vertical slices under `Features/`, its entities, logic and migrations), the UI seed (`src/Shared.Ui`,
`src/Web`, `src/Maui`) and the tests of what it owns. It is a
**continuation port** of the donor repo `vuelto-legacy/phase2` — see `docs/DECISIONS.md` ADR-V001.

## Reading order

1. [`docs/PROJECT_BRIEF.md`](docs/PROJECT_BRIEF.md) — what the app is, the core loop, the OUT list.
2. [`docs/FEATURES.md`](docs/FEATURES.md) — every flow, with sequence diagrams for the two hot paths.
3. [`docs/DATA_MODEL.md`](docs/DATA_MODEL.md) — entities, ER diagram, derived rules, lifecycles.
4. [`docs/DECISIONS.md`](docs/DECISIONS.md) — the app's `ADR-V…` series (the platform's ADRs are the platform's log).
5. [`docs/README.md`](docs/README.md) — the docs index: the platform's documents (WAYS_OF_WORKING: slices, stories, TDD,
   PRs) and vuelto's specifics (ports, staging, OAuth, the operator runbooks).
6. [`CLAUDE.md`](CLAUDE.md) — the operating manual (golden rules, conventions, doc map).

The platform's own documents (architecture, flows, deployment, localization, the new-app guide) live in the platform
repo; [`docs/README.md`](docs/README.md) links them.

## Run it locally

Operator tools (hidden — no page, no route), e.g. moving your household to another server:
[`ops/README.md`](ops/README.md).


```bash
cp .env.example .env            # then fill it — at minimum Jwt__Secret (any ≥32-char string) and PLATFORM_PACKAGES_TOKEN
dotnet tool restore             # the platform tool: `dotnet platform …` (ports, e2e, qa-guide, publish-native)
docker compose up -d db mail    # Postgres 17 on :5434 + Mailpit on :1026 (SMTP) / :8026 (UI)
dotnet run --project src/Host --launch-profile https   # API on https://localhost:7260
dotnet run --project src/Web                           # web UI on https://localhost:7108
```

Sign in with **"Email me a 6-digit code"** and read the code from Mailpit at
<http://localhost:8026>. The compose project is named `vuelto` and uses its own ports so it
runs alongside the other two apps — see [Local ports](#local-ports).

```bash
dotnet test tests/Api.Tests     # spins up a Postgres Testcontainer; Docker must be running
dotnet test tests/Ui.Tests
node --test tests/ui-js/*.test.js
dotnet platform e2e             # the browser journeys against a local stack (-Gates for the gates-off lane)
```

## Local ports

The three apps run side by side on one machine, each on its own block, so a stack never answers for another. This
table is the same in all three repos. A repo states its block ONCE, in `local-ports.props`: code reads it through the
generated `LocalPorts` class, `dotnet platform ports -Apply` writes it into the files that cannot read MSBuild (compose
defaults, launch profiles, appsettings, `.env.example`, the Postman environment, this row), and `LocalPortsTests` fails
on any file that disagrees, on a port number in code, and if two apps share a port.
API http is the port the Android emulator uses; the app container is `docker compose --profile app up`.

| App | Postgres | Mailpit SMTP | Mailpit UI | API https | API http | Web https | Web http | app container |
|---|---|---|---|---|---|---|---|---|
| perezosoft-platform | 5433 | 1025 | 8025 | 7160 | 5238 | 7008 | 5169 | 8080 |
| **y-el-vuelto** (this repo) | 5434 | 1026 | 8026 | 7260 | 5338 | 7108 | 5269 | 8180 |
| jigger-jot | 5435 | 1027 | 8027 | 7360 | 5438 | 7208 | 5369 | 8280 |

## Branches

`main` is deploy-only (protected); `develop` is the working branch — one branch + one PR per slice,
Conventional Commits, the PR template in `.github/`. CI must be green before merge.
