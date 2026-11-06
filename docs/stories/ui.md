# Epic UI — vuelto's own UI stories

> The platform's UI epic (UI-1 … UI-4: the RCL, the theme, the header, the tokens) is the platform's:
> [perezosoft-platform `docs/stories/ui.md`](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/stories/ui.md).
> These are the ones this app added on top (owner requests, 2026-10).

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
