# Project Conceptualization Primer

> **How to use this:** Paste this file (or its contents) at the start of a new project chat with
> Claude. It pre-loads the *constant* technical decisions and the working method so the
> conversation can jump straight to **what the app does**, not how it's built. Claude should read
> this, then begin conceptualizing the new app with you.

---

## Instructions to Claude

You are helping me conceptualize a **new multi-tenant SaaS application** from scratch. The
**tech stack and architecture are already decided** (see "Frozen decisions" below) — do not
re-litigate them unless I explicitly raise a reason to. Your job is to help me work out the
**app-specific** parts: concept, features, data model, and scope.

**Run the session like this:**

1. **Start by understanding the app.** Ask me what the app is and who it's for. Ask focused
   clarifying questions — ideally one cluster at a time, not a wall of them. Prefer concrete
   options over open-ended prompts when you can.
2. **Make recommendations, not just questions.** When there's a sensible default, propose it and
   say why, then let me confirm or redirect. Push back when something seems off; I want honest
   guidance, not agreement.
3. **Surface tensions early.** Call out modeling forks, scope ambiguities, and decisions that are
   expensive to reverse *before* they get baked in.
4. **Record every settled decision as an ADR** in `DECISIONS.md` (decision + rationale + date).
   The point is so neither of us re-debates settled choices later.
5. **Be disciplined about scope.** Maintain an explicit "OUT / deferred" list. When I float
   something beyond MVP, park it as a pin rather than absorbing it silently.
6. **Produce the doc set** (templates provided): `PROJECT_BRIEF.md`, `FEATURES.md`,
   `DATA_MODEL.md`, `TECH_STACK.md` (mostly pre-filled), `DECISIONS.md`, and the Claude Code
   `CLAUDE.md`. Fill app-specific sections; leave the frozen sections as-is.
7. **Know the chat/Claude Code split.** Conceptualization, decisions, curation, and sequencing
   happen here in chat. Building in the repo happens in Claude Code. Flag the handoff point when
   the thinking layer is complete (concept + features + data model + decisions settled).
8. **Verify volatile facts.** Before stating current tool/library versions, search — don't rely
   on training data. Apply the "latest stable, never previews" policy.

**Do NOT carry over app-specific modeling from prior projects.** Each app's entities, derived
rules, and domain logic are designed fresh. Only the items under "Frozen decisions" are constant.

---

## Frozen decisions (constant across my SaaS projects — do not re-decide)

### Product shape
- **Multi-tenant SaaS.** There is always a **Tenant** (an org / household / team / workspace —
  the exact label is app-specific) and **Users** belonging to it. Multiple users per tenant.
- **Tenant-scoped data; per-user preferences only.** App data belongs to the tenant and is shared
  among its users. Only individual preferences (e.g. display settings) are per-user. Never leak
  one tenant's data to another.
- **Web first for features.** MAUI mobile + Win/macOS desktop shells ship with the platform (auth
  wired); build each feature on web first.

### Tech stack (target latest STABLE, never previews — verify versions at session time)
- **Backend:** ASP.NET Core Web API, behind a **clean API boundary** (no UI-to-DB direct access).
- **Web frontend:** Blazor WebAssembly.
- **UI components:** live in a **shared Razor Class Library (RCL)** — never inline in the web
  app. This is the rule that makes future non-web clients cheap.
- **Database:** PostgreSQL.
- **ORM:** Entity Framework Core (Npgsql provider).
- **Auth:** custom JWT access tokens + rotating refresh tokens — the platform ships this (**not**
  ASP.NET Core Identity); tenant scoping layered on top as a query concern.
- **Baseline version line:** .NET 10 (LTS) and its matching ASP.NET Core / Blazor / EF Core.
  **Re-verify the current stable versions at the start of each project** (this primer ages).

### Architecture principles
- **The API is the durable asset.** Every client (web now; mobile + desktop later) is just
  another consumer of the same API. New client types are *additive*, never a rearchitecture.
- **RCL discipline** (above) — shared UI components across web + future clients.
- **Non-web clients:** .NET MAUI **Blazor Hybrid** shells for **mobile and Windows/macOS desktop**
  ship with the platform (auth wired, reusing the RCL — see `docs/MOBILE_TESTING.md`). Build app
  features web-first and extend the native shells once they work. Linux desktop is out of scope;
  if ever required, tilt toward Uno Platform or Avalonia.

### Working method & docs
- The document set and ADR methodology described in the instructions above are constant.
- Chat = thinking (concept, decisions, curation, sequencing). Claude Code = building in the repo.
- User stories are generated **per-epic at build time**, not all upfront.

---

## What we'll figure out together (app-specific — nothing pre-decided)

- What the app is, who it's for, the core loop / headline value.
- The tenant label (org? household? team? workspace?) and any team/role nuances.
- Feature scope: MVP IN vs. deferred OUT.
- The data model: entities, relationships, and any **derived rules** specific to this domain.
- UX/UI direction.
- Seed data, if the app needs a curated starting dataset.
- Hosting specifics (deferred to near-deploy).

---

## Suggested first move

Ask me to describe the app in a few sentences, then begin clarifying. Once concept + features +
data model + decisions are settled, remind me it's time to create the repo (using the starter
skeleton) and switch to Claude Code — where the **first task is the rebrand** (name, logo,
logo-derived colour palette, OAuth scheme). The repo's `README.md` contains the expected
copy-paste first prompt for that session; make sure I leave the conceptualization with an app
name, a tenant label, and a logo file in hand.
