# ¿Y el vuelto?

Personal finance for Costa Rican **households that live in two currencies** (₡ CRC + $ USD).
Every expense is captured in both currencies at that day's rate, budget months follow the
household's **pay cycle** (weeks anchored on a chosen weekday, not the calendar), and bank voucher
emails from BAC/BN become transactions after a one-click review.

The name is Costa Rican slang — *"¿Y el vuelto?"*, "and the change?". The code, database and
docs keep the name **`Vuelto`**; the display brand is *¿Y el vuelto?* — that split is intentional.

Built on the [Perezosoft platform](https://github.com/argamboad/perezosoft-platform) (ASP.NET Core
API + Blazor WASM + shared RCL + PostgreSQL/EF Core + custom JWT auth, with MAUI Blazor Hybrid
shells). Multi-tenancy, auth, invitations, MFA, billing, jobs, notifications, GDPR and the deploy
pipeline are inherited; this repo adds the budgeting domain as vertical slices. It is a
**continuation port** of the donor repo `vuelto-legacy/phase2` — see `docs/DECISIONS.md` ADR-V001.

## Reading order

1. [`docs/PROJECT_BRIEF.md`](docs/PROJECT_BRIEF.md) — what the app is, the core loop, the OUT list.
2. [`docs/FEATURES.md`](docs/FEATURES.md) — every flow, with sequence diagrams for the two hot paths.
3. [`docs/DATA_MODEL.md`](docs/DATA_MODEL.md) — entities, ER diagram, derived rules, lifecycles.
4. [`docs/DECISIONS.md`](docs/DECISIONS.md) — the platform's ADRs (inherited) + the app's `ADR-V…` series.
5. [`docs/WAYS_OF_WORKING.md`](docs/WAYS_OF_WORKING.md) — slices, Gherkin stories, TDD, PR conventions.
6. [`CLAUDE.md`](CLAUDE.md) — the operating manual (golden rules, conventions, doc map).

Platform mechanics (architecture, flows, deployment, localization, QA plan) are documented in
`docs/` as inherited from the platform; `docs/NEW_APP_GUIDE.md` is the phase-by-phase path this
repo is following.

## Run it locally

```bash
cp .env.example .env            # then fill it — at minimum Jwt__Secret (any ≥32-char string)
docker compose up -d db mail    # Postgres 17 on :5434 + Mailpit on :1026 (SMTP) / :8026 (UI)
dotnet run --project src/Api --launch-profile https    # API on https://localhost:7160
dotnet run --project src/Web                           # web UI on https://localhost:7008
```

Sign in with **"Email me a 6-digit code"** and read the code from Mailpit at
<http://localhost:8026>. The compose project is named `vuelto` and uses its own ports so it
runs alongside the platform's own stack (5433 / 1025 / 8025).

```bash
dotnet test tests/Core.Tests
dotnet test tests/Api.Tests     # spins up a Postgres Testcontainer; Docker must be running
dotnet test tests/Ui.Tests
```

## Branches

`main` is deploy-only (protected); `develop` is the working branch — one branch + one PR per slice,
Conventional Commits, the PR template in `.github/`. CI must be green before merge.
