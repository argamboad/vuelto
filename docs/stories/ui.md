# Stories — platform web UI wave (`UI`) — RETROSPECTIVE

> **Written after the fact** (v3 audit T59, closing v2 finding DOC-22/B10-4): the four UI slices
> below shipped during the v2-audit remediation window (2026-07-01/02) **without a story file**,
> violating WoW's "story file before an epic". The QA plan (§2) and traceability matrix already
> cite UI-1..4; this file is the missing definition they point at. Content reconstructed from the
> shipped code, the QA cases that cover each slice, and the commits noted below — statuses are
> historical fact, not plans.

**Epic key:** `UI` · **Status: ✅ COMPLETE (retrospectively documented)**

| # | Story | Shipped as | Manual QA coverage |
|---|-------|-----------|--------------------|
| UI-1 | **GDPR surfaces get a web UI** — owner data export (Household → Data) + account erasure (Settings → Danger zone) over the existing GDPR-1/2 APIs | commit `1157ecb` | QA-HH-13, QA-SET-07 |
| UI-2 | **MFA gets a web UI** — authenticator enrollment/confirm/disable in Settings (QR + manual secret, recovery codes shown once) and the sign-in step-up on Login | commit `bf61aaf` | QA-MFA-01..03 |
| UI-3 | **Notification center gets a web UI** — the header bell (list, unread badge, mark-read, delete/clear) + Settings delivery-preference switches over NOTIFY-1/2 | commit `c867f01` | QA-NOTIF-01..04 |
| UI-4 | **Staff admin console** — the config-gated `/admin` surface (tenant list/detail, impersonation, targeted/broadcast announcements, plan comp/revert, MFA reset) over ADMIN-1..3 | commit `65ea677` | QA-ADMIN-01..07 |

**Why retrospective, and the rule it reinforces:** these slices were built inside the v2
remediation push where the epic-with-story cadence was (wrongly) skipped as "just UI over existing
APIs". The WoW rule stands: *every* epic gets its story file **before** implementation — a UI wave
included. E2E follow-ups for these surfaces were planned and delivered as the separate `E2E` epic
(`docs/stories/e2e.md`).
