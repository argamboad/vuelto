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

### UI-5 — Cards fold to their heading, and stay folded *(owner request, 2026-10-09 · #205)* ✅

**As** a household member, **I want** to fold the cards I don't need on busy screens, **so that** the ones I use are
within reach — and **I want** them to stay folded next time.

**Context / notes:** one RCL component, `CollapsibleCard` — no chrome of its own (the page keeps its card element and
scoped styles); it renders the heading row with a `<button aria-expanded aria-controls>` named "Collapse/Expand
<card>" and the body, `hidden` while folded, with an optional one-line summary in its place. State per device in the
`appUi` prefs under `collapse.<page>.<card>`; no storage → everything opens. Used on Settings (all eight cards),
Reports (pace + every chart card), Household (Members, Invitations, Data) and the Dashboard (Fixed, Variable, "Where
it went" — `BudgetLineList` / `BreakdownPanel` take a `CollapseKey`). The KPI tiles, the verdict and the waterfall
stay open: they are the page's answer, not detail.

```gherkin
Scenario: Fold and come back
  Given I fold Catalog on Settings
  When I reload
  Then Catalog is still folded and the other cards are open
```

### UI-6 — Phones never scroll a page sideways *(owner report, 2026-10-09 · #209)* ✅

**As** a household member on a phone, **I want** every page to fit the screen, **so that** I don't drag tables around
to read them.

**Context / notes:** a mobile browser that meets content wider than the screen widens the whole layout, so one wide
table made every page feel broken. Measured at 375 px with real rows: the dashboard's week cut (490 px — four "₡x · $y"
columns once #211 added Unplanned), its bank cut (569 px), the month header (the #207 switch pushed "New transaction" off
the edge), the #208 Refunds table (466 px) and the Household's long member emails. Fixes: every table sits in a
`.table-responsive` box (a table may scroll inside its card, the page never does); on a phone the dashboard's breakdown
cells stack ₡ over $ (`Pair`), headers shrink and the panel takes the card's side padding; budget lines put the name on
its own line; the month header wraps; the Refunds rows fold date, month and status under the payee; member and
invitation emails wrap. Desktop is unchanged. `PhoneLayoutTests` (E2E) seeds a household through the API and asserts
no main page is wider than a 375 px screen, the dashboard's four cuts included.

```gherkin
Scenario: The dashboard on a phone
  Given a month with budgeted, discretionary and unplanned spending
  When I open the dashboard at 375 px
  Then the page cannot be dragged sideways, and "By week" shows all four columns with ₡ over $
```
