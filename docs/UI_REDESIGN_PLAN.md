# UI redesign — plan

> **Status (2026-09-14): PR #63 open (`feat/ui-redesign` → `develop`) — all 11 slices built and committed
> (SKIN-1 tokens · 2 components · 3 login · 4 shell · 5 dashboard · 6 review · 7 transaction form · 8 months and month detail · 9 budget · 10 reports · 11 the fourteen-screen sweep),
> plus three fix commits found by looking (dark-theme anchor buttons, disabled primary buttons, the
> stated rhythm/radius applied as one source). Nothing pushed; the PR to `develop` is the next step.** The source is `UI Handout.pdf`
> (31 pages, "Seven screens, three widths, two themes", prepared 2026-09-11, produced with Claude
> Design). This file is the reading of that handout against *this* codebase: what it gets right,
> what it doesn't know, what order to build it in, and what it costs.
>
> The handout's own claim, which this plan holds it to: *"Nothing here changes the domain model,
> the API surface, or the pay-cycle logic — this is a presentation-layer redesign of pages that
> already work."* Every per-screen "Do not change" row in the handout is already true in the code;
> none of them is at risk.

---

## 1. What the handout actually contains

Eight screens, each at three widths (desktop 1280 = Bootstrap `lg`, tablet 834 = `md`, phone 390 =
below `md`, which is the MAUI shell) in both themes, plus a one-page build-notes sheet per screen
covering files, Bootstrap-vs-custom, tokens, breakpoints, strings, state, accessibility, and a
"do not change" list.

**The five decisions it is built on**

1. One verdict per screen, stated at display size, before any table.
2. Dual currency stops doubling every cell — the active currency is primary and large, the other
   sits muted underneath.
3. Money is always `font-variant-numeric: tabular-nums` and right-aligned.
4. Green and red mean under/over budget and nothing else. Gold `#F2CB6E` stays logo-only. Amber
   means "needs attention", never "over budget".
5. Say what an action will do before it happens.

**The six substitutions**

| Out | In |
|---|---|
| Ten-row dashboard waterfall | One verdict + a four-step row |
| Middot-doubled currency in every cell | A display toggle, stacked money |
| budget / actual / total triplets | Progress rows with a signed delta |
| Three breakdown cards | One panel behind a segmented switch |
| Twelve stacked form fields | Amount-first, three groups, a consequence rail |
| Review form with Confirm at the bottom | Two-pane rows with a visibly blocked Confirm |

**Kept exactly:** pay-cycle months and week windows, per-transaction frozen rates, the expense-class
taxonomy, refunds separate from income, household sharing, review-before-booking.

---

## 2. Ownership: every file this touches belongs to this app

Several files the handout names also exist in `perezosoft-platform` — `wwwroot/css/app.css`,
`Components/AppHeader.razor`, `Layout/MainLayout.razor`, `Pages/Login.razor`. That overlap is **not**
shared ownership, and it does not route any of this work upstream.

**The rule (owner, 2026-09-11): the platform seeds a UI; it does not own one.** Once an app is
created from the platform, `src/Shared.Ui` belongs entirely to that app — every page, component,
layout and token in it. The platform's copy is a starting point, not a baseline to stay in step
with. `NEW_APP_GUIDE.md` Phase 3 already assumes this: rebranding an app means editing its own
`app.css` in its own repo, in the first session.

What *is* shared is the **backend**. There the rule is stricter than "don't modify": the platform's
backend can be **extended through its seams and have parts disabled**, but changing its baseline is
a no, or at minimum a discussion before any code. Nothing in this redesign touches the backend, so
that rule never comes into play here.

**Consequence for this plan: all eleven slices are app-side work in this repo. There is no platform PR
and no `chore/sync-platform-NNN` sync anywhere in the sequence.** That restores the handout's own
eight-step order, which the repo boundary would otherwise have cut in two.

Two tokens the handout lists already exist here and must not be redefined: `--app-bg` and
`--app-border`, in both the `:root` and the `[data-bs-theme="dark"]` blocks. The rest of the set
(`--card-bg`, `--rail`, `--hair`, `--zebra`, `--ink`, `--ink-2`, `--ink-3`, `--good`, `--bad`,
`--warn-bg`) is new here and is defined here.

---

## 3. Five things in the handout that are stale or wrong for this codebase

**a) The test warning is aimed at the wrong suite.** The handout warns that the E2E journeys assert
on visible text and roles, and that selectors must be updated in the same commit as the screen.
They don't. All 48 selectors across `tests/E2E.Tests` are `GetByTestId`, and the only two pages the
E2E suite ever navigates to are `/settings` and `/household` — neither of which is being
redesigned. E2E risk is effectively nil.

The real test surface is **bUnit**: 71 tests across eight files hang off these screens
(`DashboardPageTests` 9, `ReportsPageTests` 14, `ReviewPageTests` 12, `LedgerPagesTests` 15,
`BudgetPageTests` 7, `RefundPagesTests` 5, `ChartComponentsTests` 5, `ReviewBadgeTests` 4), and the
eight pages carry **193 `data-testid` anchors** between them. Those anchors are the safety net:
keep a testid on the element that carries the same meaning after the redesign and the test survives
the rewrite. Budget that into each screen's commit.

**b) Nunito is already loaded.** `src/Web/wwwroot/index.html` links the Nunito family at weights
400, 600, 700 and 800 — exactly the four the handout's type scale asks for. The typography work is
a scale-and-weight exercise, not a font-loading one.

**c) The dashboard it redesigns is missing two sections the live one has.** The new dashboard
covers the verdict, the pace bar, the four-step waterfall, the fixed/variable line lists, and a
"Where it went" panel switching **By week | By bank | By card** — which correctly absorbs the
weekly rows, the bank × method table (DASH-1) and the by-card table (CARDS-2). But **Envelopes**
and **Other spending** (categories with actuals and no budget line) appear on no frame at any
width. Both are shipped, QA-covered features. Proposal in §7.2; proposal settled at that slice’s gate (§4).

**d) The same problem on Reports, larger.** The live Reports page has a Table/Chart view toggle and
nine chart instances: class-split donut, income-split donut, budget-split donut, pace line chart,
month-by-month trend bars, by-bank donut, by-card donut, by-method donut, and method budget bars.
The handout's Reports screen is four KPI tiles, one pace chart, and a category table with inline
bars. That is a large, unacknowledged reduction — REPORTS-3 and REPORTS-4 shipped most of those
charts deliberately. Proposal in §7.1; proposal settled at that slice’s gate (§4).

**e) The class split stops being money.** Today the waterfall carries three muted money rows —
`Dash_WfBudgeted`, `Dash_WfDiscretionary`, `Dash_WfUnplanned` — so "how much went to discretionary
this month" is answerable at a glance, in colones. On the new dashboard the four-step row is Income
/ Spent so far / Still planned / Forecast, and the class split survives **only as percentages in
the pace bar's legend** ("Budgeted 51% · Discretionary 12% · Unplanned 4%"). No frame at any width
shows a class amount in money.

That taxonomy is the spine of this app — it is what separates it from a spreadsheet — and a
percentage of a bar is a weaker instrument than an amount when the question is whether to cut back.
Cheapest fix, and the one that keeps the handout's own "one verdict, then the detail" shape: make
the legend carry the amount beside the percentage, or make "Spent so far" expand to the three class
rows. Proposal in §7.3; proposal settled at that slice’s gate (§4).

---

## 4. The build order — epic `SKIN`, eleven slices, one PR

**Shipping shape (owner, 2026-09-11): one branch, one PR, each slice its own commit.** Not ten PRs.
The reason is latency — ten review-and-merge round trips for a redesign that has to be judged as a
whole is an eternity between changes, and the halfway states are worth nothing to anyone. It also
costs one CI pipeline run instead of ten, which the proportional-CI rule (LOCALCI-3) rewards.

Branch `feat/ui-redesign`, Conventional Commits, one commit per row below.

| # | Commit | Notes |
|---|---|---|
| SKIN-1 | **Tokens and radius** — `wwwroot/css/app.css` | Surface, ink and semantic tokens in both theme blocks; set `--bs-border-radius`. No screen changes. The acceptance test is that the *current* UI still passes in dark mode. |
| SKIN-2 | **Shared components** — stacked `MoneyDisplay` variant, `ClassChip`, `PaceBar`, segmented switch, `KpiTile`, `ConsequencePanel`, `VerdictHeader` | Built against the tokens, never literals. No screen uses them yet. |
| SKIN-3 | **Sign-in split layout** — `Pages/Login.razor` + `.css` | Indigo brand panel plus form pane; below `lg` the panel drops out and the lockup moves above the form. Unauthenticated, so it does not depend on the shell. |
| SKIN-4 | **App shell** — `Components/AppHeader.razor` + `.css`, `Layout/MainLayout.razor` + `.css` | Header nav spacing, active state, the review badge, the phone collapse. Touches every screen, so it lands before any screen commit. |
| SKIN-5 | **Dashboard** — verdict header, pace bar, four-step waterfall, line lists, "Where it went" panel | The screen that proves the system. Needs §7.2 and §7.3 settled. |
| SKIN-6 | **Review queue** — two-pane rows, the blocked-confirm rule | Highest-frequency task after the dashboard. |
| SKIN-7 | **Transaction form** — amount-first, three groups, save-preview rail | |
| SKIN-8 | **Months and Month detail** — card grid, then the ledger table with column hiding and refund rows | |
| SKIN-9 | **Budget** — reorderable lists, creation dialog, commitment header | |
| SKIN-10 | **Reports** — KPI tiles, pace chart, category table; retire `Charts/StackedBar.razor` here | Needs §7.1 settled. |
| SKIN-11 | **The rest of the app** — conform the fourteen screens the handout never drew to the system the first ten build | §6. No new design decisions; the largest slice by screen count and the smallest by risk. |

**Why this order:** it is the handout's own — tokens, then components, then shell, then the screen a
user meets most often, down to the least — with Login pulled forward to third. It is the simplest
screen of the eight, it has no data dependencies, and it does not sit under the shell, so it works
as a low-risk worked example of the token system before the shell moves under everything else.
Reports sits last because it carries the biggest open design question (§7.1).

**Keep every commit green.** The handout's slicing exists so each step leaves the app working. In a
single PR that property does not come for free, and it is worth preserving deliberately: every
commit compiles and the full suite passes on its own. That is what makes a later `git bisect` or a
single-slice revert possible once this is one merge commit on `develop`.

### Working agreement — a gate before every slice

**Owner, 2026-09-11: before starting a slice, show the handout pages for it and state what will
change. Work begins only on an explicit green light.** This is what makes the single PR safe — a
single PR removes the nine natural review pauses, and this gate puts them back without paying for
ten review round trips.

It also relaxes the design questions. They are no longer all due up front; each is due at the gate
of the slice that needs it. **§7.2 and §7.3 at the Dashboard gate (SKIN-5); §7.1 at the Reports
gate (SKIN-10).** Nothing blocks SKIN-1.

**Which pages to show.** The handout is front matter then three pages per screen, in a fixed shape:
desktop frames, then tablet and phone frames, then the build-notes sheet.

| Slice | Handout pages |
|---|---|
| SKIN-1 Tokens and radius | 3 (tokens), 4 (type and radius) |
| SKIN-2 Shared components | 5 (component inventory) |
| SKIN-3 Login | 29, 30, 31 |
| SKIN-4 App shell | 7 (build sequence) — the shell has no frames of its own; it is visible in the header of every screen frame |
| SKIN-5 Dashboard | 8, 9, 10 |
| SKIN-6 Review queue | 23, 24, 25 |
| SKIN-7 Transaction form | 17, 18, 19 |
| SKIN-8 Months and Month detail | 11, 12, 13 and 14, 15, 16 |
| SKIN-9 Budget | 26, 27, 28 |
| SKIN-10 Reports | 20, 21, 22 |
| SKIN-11 The rest of the app | **none — the handout never drew these.** Its gate is the conformance checklist in §6 plus the already-built screens as the reference |

Every page of the PDF is image-only, so the pages are rendered to PNG before they can be read or
shown. `scratchpad/render_handout.py` does it with PyMuPDF in one pass; poppler and `pdftoppm` are
not installed on this machine, and pypdf's image extractor yields useless sliced fragments.

**One sequencing trap:** `Charts/StackedBar.razor` is used by **both** Dashboard and Reports. The
handout says `PaceBar` supersedes it. It cannot be deleted in SKIN-5 — it dies in SKIN-10, when
Reports no longer imports it.

---

## 5. What this costs, in this repo's currency

| Surface | Today | Change |
|---|---|---|
| Localization keys | 683 in `AppStrings.resx`, mirrored in Spanish | **63 new keys** named by the handout, so 126 resx entries. `LocalizationKeyCoverageTests` gates the pair. |
| bUnit tests | 71 across 8 files on these screens | Re-point selectors per slice; keep the 193 `data-testid` anchors wherever the meaning survives. |
| E2E tests | 48 testid selectors, `/settings` and `/household` only | **No change.** |
| QA plan | 191 cases | A slice-by-slice mapping, and what the artifact gates force, is §5b. |
| Native | `QA-AND` 15, `QA-DSK` 15, `QA-IOS` 4, `QA-MAC` 3 | The MAUI shells render the same `Shared.Ui`, so the handout's phone frames **are** the native app. Re-walk the native cases once the shell commit is in. |
| Docs | — | `docs/FEATURES.md` describes several of these flows by their current shape; this file needs its doc-map row. |

The 63 new keys, by screen: Dashboard 10, Months 6, Month detail 9, Transaction form 13, Reports 8,
Review 6, Budget 8, Login 3.

---

## 5b. The QA plan is part of every slice, not a cleanup at the end

**Owner, 2026-09-11: know exactly how each slice moves the QA plan, and change it with the slice.**
`docs/QA_TEST_PLAN.md` is a manual plan of 191 cases written as Gherkin plus a click-by-click
walkthrough. A walkthrough names buttons, columns and orders. This redesign changes buttons, columns
and orders on eight screens. So the plan is not documentation *about* this work — it is an output of
it.

### What the gates actually require

| Gate | Trigger | What it forces |
|---|---|---|
| `qa-artifacts` CI job | **any** edit to `QA_TEST_PLAN.md`, prose included | It regenerates both PDFs and diffs their *text* against the committed ones. `QA_TEST_GUIDE.pdf` and `QA_RUN_LOG.pdf` must be regenerated (`python gen_qa_guide.py`, `python gen_qa_runlog.py` in `docs/`) and committed. Note this job **never gates on `code`** — it runs on docs-only pushes too. |
| `signoff_drift()` in `check_qa_artifacts.py` | adding or removing a case | Every case needs exactly one sign-off row, in §16 or (for `QA-ADV-*`) §14a, never both. |
| `EnforcementGateTests.ClaudeMdQaCaseCount_MatchesTheQaPlan` | adding or removing a case | The `(N cases:` figure in `CLAUDE.md`'s doc map must match the `### QA-` count. |
| §15 traceability matrix | adding or removing a case | Feature → cases → API rows kept in step. |

**Renaming a case title is free** — the sign-off sheet and matrix key on the id, not the words. Only
adding or removing a case touches three files at once.

**Where the PDF regeneration goes.** Regenerating two PDFs inside all eleven commits is eleven
binary churns. Recommendation: **edit the plan's prose inside the slice commit that causes it, and
regenerate both PDFs once in a final `docs(qa)` commit.** The artifacts job only ever runs against
the pushed head, so nothing is red at the end; the only cost is that a mid-PR push would show that
one job red until the last commit lands.

### Which cases each slice moves

| Slice | Cases | The interesting part |
|---|---|---|
| SKIN-1 Tokens | none directly | `QA-SET-08`, `QA-DSK-15`, `QA-AND-14` are the dark-theme cases and are the *acceptance evidence* for this slice, unchanged. |
| SKIN-2 Components | none | Nothing user-visible yet. |
| SKIN-3 Login | `QA-AUTH-08/09/10`, `QA-I18N-01`, `QA-SMK-01/02`, `QA-IOS-01`, `QA-MAC-01` | The language and theme selects move onto the form pane; `QA-I18N-01` walks exactly that. |
| SKIN-4 App shell | `QA-AND-05`, `QA-AND-13`, `QA-NOTIF-01`, `QA-SET-08` | `QA-AND-05` is literally "navigation drawer / header is reachable" — the phone collapse changes its steps. |
| SKIN-5 Dashboard | `QA-DASH-01`…`05`, `QA-FX-01`, `QA-ENV-01/02`, `QA-EMAIL-06` | **`QA-DASH-04`'s own title names what is being removed** — "the stacked bar and the waterfall down to the forecast". It is rewritten, not adjusted. `QA-DASH-05` becomes the "Where it went" panel. If §7.2 lands, the dashboard half of `QA-ENV-01/02` becomes the conditional strip. |
| SKIN-6 Review queue | `QA-EMAIL-05/06/07` | Confirm is now blocked visibly rather than failing on submit — a new *Then* in `QA-EMAIL-06`, not a reworded one. |
| SKIN-7 Transaction form | `QA-LED-01/02/04/05/07`, `QA-CAT-05/07`, `QA-FX-01` | Field order changes, so every walkthrough step order changes. The consequence rail is new behaviour worth its own *Then*. |
| SKIN-8 Months + Month detail | `QA-LED-01`…`08`, `QA-EMAIL-07`, `QA-REP-02` | Below `md` the ledger hides columns instead of scrolling sideways; the phone steps change, the desktop ones mostly do not. |
| SKIN-9 Budget | `QA-EXP-01/02/03` | **`QA-EXP-02` is the one case whose *steps* stop existing.** Its title is "Reorder with ▲▼" and its Gherkin says "When I click ▼ on Mortgage". The handout retires those buttons for a drag handle plus a keyboard move menu. New title, new Gherkin, and the keyboard path is a genuinely new assertion. |
| SKIN-10 Reports | `QA-REP-01/02/03/04` | `QA-REP-03` and `QA-REP-04` *are* the eight charts, case by case. §7.1's answer decides whether they are rewritten or deleted — and deleting two cases is the one place in this PR that moves the case count, §16 and `CLAUDE.md` together. |
| SKIN-11 The rest | `QA-HH-*`, `QA-SET-*`, `QA-BILL-*`, `QA-ADMIN-*`, `QA-INV-*`, `QA-CAT-*`, `QA-ENV-*`, `QA-EMAIL-01/02/03`, `QA-NOTIF-*`, `QA-MFA-01/03` | Mostly prose. The exception is the consequence panels on the destructive actions (§6): `QA-HH-07` dissolve, `QA-SET-07` delete account, `QA-ADMIN-06` comp/revert each gain a *Then* asserting the consequence is stated before the confirm. |
| Native re-walk | `QA-DSK-*` 15, `QA-AND-*` 15, `QA-IOS-*` 4, `QA-MAC-*` 3 | The MAUI shells render this same UI, so the "core flows spot-check" cases re-walk redesigned screens. Their *text* rarely changes; they need re-running, which is the owner's manual pass, not a code change. |

**Expected net case count: unchanged at 191**, unless §7.1 retires the Reports chart cases. That is
the only decision in this PR that can move the number, and if it does, `CLAUDE.md`, §15 and §16 move
with it in the same commit.

---

## 6. SKIN-11 — the fourteen screens the handout never drew

The handout redesigns 8 screens. This app has **22**. **Owner, 2026-09-11: the other fourteen get a
slice of their own, in the same PR, so the app reads as one product rather than two.**

The drift they would otherwise show is structural, not chromatic. The tokens are CSS custom
properties and the radius is `--bs-border-radius`, so every untouched screen inherits the new
surface, ink and corner treatment for free the moment the tokens commit lands. What none of them
inherits is the new card, table and chip *composition* — which is exactly what makes a screen read
as older rather than merely different.

### Scope — fourteen, not four

The owner named the platform-seeded four: Household, Settings, Billing, Admin console. Taken
literally that leaves Cards, Envelopes, Email settings, Merchant mappings and the catalog pages
looking old, which defeats the stated goal. **So this slice covers all fourteen**, and the extra ten
are nearly free. Say so at the gate and cut it back to four if that is not wanted.

| Screen | Size | Note |
|---|---|---|
| Household | 538 lines, 22 testids | The biggest, and the one with the destructive actions |
| Admin console | 476 lines, 17 testids | Config-gated; lowest user frequency of the fourteen |
| Email settings | 451 lines, 34 testids | |
| Cards | 339 lines, 32 testids | |
| Settings | 312 lines, 11 testids | |
| Envelopes | 273 lines, 23 testids | |
| Billing | 253 lines, 11 testids | |
| Merchant mappings | 212 lines, 12 testids | |
| Join | 132 lines, 9 testids | |
| Banks, Categories | 24 lines each | **Both delegate to `Components/CatalogPage.razor`** — restyle that one component and both conform |
| Home, Auth callback, Auth error | 78 lines combined | Near-trivial |

### The conformance checklist — what "aligned" means

No handout frames exist for these, so this slice is judged against a checklist rather than a
picture. Every item is derived from the handout's own five decisions and component inventory:

1. **Page header** — title and subtitle in the new type scale, matching every redesigned screen.
2. **Card shell** — `--card-bg` on `--app-bg`, radius 14, a single `--app-border` hairline. No cards
   nested inside cards.
3. **Tables** — `--zebra` header row, `--hair` row rules, `--zebra` on hover, money right-aligned in
   `tabular-nums`.
4. **Controls** — radius 9, the new input surface tokens, chips as the `ClassChip` pill rather than a
   raw Bootstrap badge.
5. **Money** — the stacked `MoneyDisplay` everywhere; the doubled middot form appears nowhere.
6. **Semantic colour** — green and red for budget only, amber for attention only, gold never.
7. **Phone** — no horizontal page scroll; tables hide columns rather than scrolling sideways.

### The part of this slice that is not cosmetic

Decision 5 of the handout is *say what an action will do before it happens*, and the four
platform-seeded screens hold this app's most destructive actions: dissolving a household, leaving
one, erasing an account, changing a plan. Those are precisely where a `ConsequencePanel` earns its
keep, and today they are plain confirms. Applying decision 5 there is the highest-value work in
SKIN-11 and the reason it is not merely a paint job.

---

## 7. Design decisions needed before building, with a proposal for each

Three, and each resolves a place where the handout removes something without saying so (§3c, §3d,
§3e). All three proposals stay inside the handout's own promise — presentation only, no change to
the domain model or the API surface — which is itself the constraint that picks them.

### 7.1 · Reports: promote one chart, demote the other eight — don't delete them

The insight hiding in the handout is that it wants the **pace chart** full width at the top of the
page. That chart already exists here as `rep-pace-chart`. So the handout is not adding a chart; it
is promoting the one chart that answers *"am I all right?"* out of the chart view and into the
always-visible header. The eight that answer *"where exactly?"* are a different question and belong
one click away, not gone.

**Proposal.** Keep the existing Table/Chart toggle and move it below the new header, where it now
governs only the lower half of the page.

- **Always visible:** the four KPI tiles, then the full-width pace chart. This is the Reports
  screen's verdict.
- **Table view (default):** the handout's category table with inline bars and the class switcher.
- **Chart view:** the eight donuts and the month-by-month bars, unchanged in content, restyled onto
  the SKIN-1 tokens.

Cost is close to nothing. The charts already exist and already take a currency parameter; SKIN-1
gives them the new palette for free. SKIN-9 becomes "new header, new category table, restyle the
chart view" rather than "delete eight charts".

### 7.2 · Dashboard: the two orphans are two different problems

**Other spending** is genuinely "where the money went" — expense-class money in categories with no
budget line — so it belongs in the panel. **Proposal: a fourth option on the segmented switch,
By week | By bank | By card | Unbudgeted.** It renders the `other_spending` array the summary
endpoint already returns, so there is no API change, and the orphan card on today's dashboard
disappears. Widening it to *every* category rather than only the unbudgeted ones would read better
still, but `CalculateOtherSpending` in Core computes exactly the leftovers, so that version costs a
Core and API change and breaks the presentation-only promise. Not worth it here; revisit separately.

**Envelopes** are the opposite case and must *not* go in that panel. They are carved out of
expenses by design, so listing them under "where it went" actively misleads. They are also
**conditional** — the `five_week_months` cadence exists precisely so a bucket surfaces in the
months it is due and stays quiet otherwise.

**Proposal: a conditional strip, one thin row per due envelope** — name, target, contributed this
month, remaining, as a progress row — sitting below the four-step waterfall and above the line
lists. It renders only when a bucket is due by its cadence. Two gains over today: the cadence
finally does something visible, and in the months nothing is due the dashboard is one whole section
shorter, which serves the handout's own goal. It goes below the verdict, not above it, because a
reminder must not displace the answer.

### 7.3 · The class split: restore the unit, don't add a row

The pace bar's legend already *is* the class split — budgeted, discretionary, unplanned, still
planned, forecast, one entry each, in the segment colours. It only lost the money.

**Proposal: put the amount in the legend beside the percentage** — `Budgeted ₡757,760 · 51%`. No
new row, no new component, no second place for the same fact. Expanding "Spent so far" into three
rows would work too, but it reintroduces exactly the stacked rows this redesign exists to remove.

If phone width fights back at five entries, the fallback is the "Spent so far" step's sub-line,
which currently spends itself on "53 transactions across 2 banks".

---

## 8. Not in this plan

No branch has been created, no code changed, no PR opened. The handout PDF itself is untracked in
the repo root; decide separately whether it belongs under `docs/brand/` or stays out of git.
