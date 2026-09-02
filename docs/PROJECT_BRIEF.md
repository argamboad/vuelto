# Project Brief

> The lean PRD. Why this exists, what it is, what it is *not* (yet). Fill the placeholders;
> the structure and the multi-tenant framing are constant.

## Elevator pitch
<!-- One or two sentences. What is this app and the single question/job it does best? -->
_TODO_

## The core loop
<!-- The primary cycle the user repeats. Everything in the product should serve this. -->
_TODO_

## Who it's for
- **Primary user:** _TODO_
- **Unit of account:** a **tenant** — _TODO: what is the tenant here? (org / household / team /
  workspace)_. Data belongs to the tenant and is shared by its users. Multiple users per tenant.

## What makes it different
<!-- The few things that distinguish this from alternatives. -->
- _TODO_

## MVP scope — what's IN
<!-- The minimal set that delivers the core loop. Keep it honest. -->
- Multi-tenant foundation: tenants, users, tenant-scoped data, per-user preferences. *(constant)*
- _TODO (app features)_

## MVP scope — what's OUT (deferred / pinned)
<!-- Good ideas deliberately parked. Record them so they're not re-debated as "forgotten." -->
- Non-web clients (mobile + desktop) — planned, not in MVP. *(constant)*
- **App signing, installers & store distribution — OUT for the platform, permanently (ADR-024).**
  Signed AAB/MSIX/IPA/pkg + store listings are per-app deliverables; each downstream app runs the
  first-native-release checklist (`NEW_APP_GUIDE.md` Phase 9). The platform proves capability via
  the CI build gate + boot smokes only. *(constant)*
- _TODO_

## Guiding principles
- **Derived, not stored.** Computed values are calculated from source data, not persisted as
  stale flags. *(general good practice — confirm per app)*
- **Tenant-scoped, not user-scoped.** Data belongs to the tenant; only preferences are per-user.
  *(constant)*
- **Clean API boundary.** UI is a client of the API; never hits the DB directly. *(constant)*
- **Lean MVP.** When in doubt, check the OUT list before building.
- _TODO: app-specific principles_

## Related docs
- `FEATURES.md` — concrete user flows and behavior.
- `DATA_MODEL.md` — entities, relationships, derived rules.
- `TECH_STACK.md` — stack & architecture (mostly constant).
- `DECISIONS.md` — ADR log.
- `../CLAUDE.md` — operating manual for Claude Code (repo root).
