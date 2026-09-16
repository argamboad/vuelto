# Plan — PDF report, emailed report, and income lines

> Planning record for three owner-requested slices discussed on 2026-09-16. Nothing here is built.
> Stories are generated per epic at build time (`docs/stories/reports.md`, new `docs/stories/income.md`);
> this file is the plan they are generated from. Decisions marked **owner** were made in the planning
> session; items under "Assumed" are recommendations the owner has not vetoed — say so and they change.

## 1. Decisions (owner, 2026-09-16)

| # | Decision | Value |
|---|---|---|
| D1 | PDF engine | **QuestPDF**, server-side. Headless Chromium rejected (Render free-tier image + memory); browser Print-to-PDF rejected (no branding, no native shells). |
| D2 | Transaction appendix | **In the first cut.** |
| D3 | "Email me this report" | **In this slice family**, not a follow-on. |
| D4 | Income model | Keep planned income per pay-cycle month (not salary-as-transactions). Replace the two hard-coded slots with an **income catalog**: one line per source, per member, own currency, fixed or variable. |
| D5 | Pay period | A line declares **how it is paid** (`weekly`, `biweekly`, `monthly`) and an amount per period; the month plan is **derived at snapshot** (weekly × week count; biweekly × paydays inside the window; monthly flat). The 4w/5w pair retires. |
| D6 | Budget unit | Unchanged: **the pay-cycle month** (ADR-V005). Pay period only affects how a line contributes to the month's plan. |

## 2. Assumed unless the owner objects

| # | Assumption | Why |
|---|---|---|
| A1 | The emailed report is an **attachment**, added upstream as a generic `EmailAttachment` on `IEmailSender` (platform PR → sync PR). | A monthly report should arrive as a file; every app benefits; the outbox already carries inline images as base64 in its JSON payload, so attachments ride the same path. |
| A2 | **Manual trigger only** ("Email me this report" in the PDF dialog). No automatic month-end send. | Render free tier sleeps; `IScheduledJob` cannot be relied on to fire on a date. Automatic sending is a later story once the host stays awake or a wake ping exists. |
| A3 | **Requester only** as recipient. | "Send to all members" is a one-line follow-on once the mechanism exists. |
| A4 | Appendix rows = **exactly the CSV export's rows** for the same period and filters, date descending, same columns. No row cap. | PDF and CSV never disagree. |
| A5 | PDF follows the on-screen **display toggle** (₡ / $ / both) and **chart currency**; language = the user's locale; paper **Letter, portrait**. | Costa Rica prints Letter. |
| A6 | UX: a **PDF** button beside **Export CSV** opens a small dialog: appendix checkbox, currency shown, two actions **Download** and **Email me**. | One endpoint builds the file; the email action queues it. |
| A7 | **Ephemeral** file: same 15-minute signed link as the CSV, no "past reports" list. The attachment is the durable copy. | Storage stays tenant-scoped and small. |
| A8 | Tests: pure document-model builder in unit tests; endpoint test asserts a valid PDF and its text (PdfPig, test-only package); one rendered PNG walked in QA. **No golden-image snapshots.** | Golden images break on every font or spacing change. |
| A9 | A per-user **daily cap on emailed reports** (e.g. 10) via the existing quota seam. | Brevo free tier is 300 mails/day; a loop or a curious member must not exhaust it. |
| A10 | Naming: **REPORTS-7** (PDF), **REPORTS-8** (email), **INCOME-1** (income lines), **INCOME-2** (income by member in reports + PDF). ADRs **V022** (PDF engine, chart builders, attachment seam) and **V023** (income lines + pay period; supersedes ADR-V003's income defaults and ADR-V005's two incomes). | Next free numbers. |

## 3. Facts the plan rests on (read from the code, 2026-09-16)

- Charts (`src/Shared.Ui/Components/Charts/{Bar,Donut,Line}Chart.razor`) are **pure inline SVG**, no JS, colored through Bootstrap CSS tokens (`var(--bs-*)`) and `var(--bs-body-font-family)`.
- **`Api` does not reference `Shared.Ui`** (only `Core` + `Infrastructure`). Rendering Razor components server-side would invert the "UI is a client of the API" boundary — so the chart **geometry moves to Core** as pure SVG builders and both sides call them (see REPORTS-7 §"Charts").
- CSV export pipeline to reuse: `ReportHandler.ExportAsync` → `IFileStorage.PutAsync` → `GetDownloadUrlAsync` (15 min) → `IFileDownloadLauncher` (web same-tab download, MAUI share sheet).
- `IEmailSender.SendAsync(to, subject, html, inlineImages?)` has **no attachment parameter**; `OutboxEmailSender` serializes `EmailOutboxPayload` (images as base64) and `SmtpEmailSender` (MailKit) sends it. Changing it is a **platform backend change** → upstream first (`perezosoft-platform`), then `chore/sync-platform-NNN`.
- Brand logo PNG already exists for email: `src/Infrastructure/Email/Assets/logo.png` — reuse for the PDF header.
- Brand typeface Nunito is loaded from Google Fonts on the web only; no font file ships in the repo. The PDF needs an embedded font **with the ₡ (U+20A1) glyph** — spike item.
- Dockerfile runtime image is `mcr.microsoft.com/dotnet/aspnet` with only `curl` added — QuestPDF's native library needs verifying there (spike item).
- Income today: `BudgetSettings.{Primary,Secondary}Income{4w,5w}` + currency; `Month.{Primary,Secondary}Income{Amount,Currency}` snapshot; `IncomeCalculator` (Core) sums both at the day's rate plus every `inflow` transaction. Dashboard, reports and the coming PDF read only `IncomeSummary`.

## 4. Slices

### REPORTS-7 — Generate a PDF report *(owner request, 2026-09-16)*

**As a** household member **I want** a branded PDF of the report I am looking at, with its tables and charts and the period's transactions **so that** I can keep, print or share the month without the app.

**Step 0 — spike (first commit of the PR, timeboxed to a session).** Exit criteria: (a) QuestPDF renders a one-page document inside the compose API container (the Render image); note any `apt` package needed in the Dockerfile; (b) an embedded font renders `₡1.500,00` — Nunito if it carries U+20A1, else a bundled fallback for money cells; (c) an SVG string from the new Core donut builder renders in QuestPDF. Findings go into ADR-V022.

**Charts — one geometry, two renderers.** New pure builders in `src/Core/Charts/` (`DonutSvg`, `BarSvg`, `LineSvg`) that take the existing models (`DonutSlice`, `BarItem`, `LinePoint` move to Core) plus a **palette** (label → color string) and return the SVG markup. The Razor components become thin wrappers that pass the Bootstrap-token palette and keep every `data-testid` the Ui tests assert. The PDF passes a **print palette** of literal brand hex values and a font family name. Rule: no `var(` may appear in a PDF SVG (asserted).

**API.** `POST /api/reports/pdf` under the existing `/api/reports` feature group. Body: period (`month_id` **or** `from`/`to`), `display` (`crc|usd|both`), `chart_currency`, `include_appendix`. Response mirrors `TransactionExportResponse`: `download_url`, `file_name` (`report-<period>.pdf`), `page_count`, `period`, `expires_in_seconds`. Errors reuse the analysis endpoint's (`rate_unavailable` sections degrade exactly as on screen: no rate → no income donuts / plan line, and the PDF says so). Postman collection updated in the same PR (parity gate).

**Document** (`src/Api/Features/Reports/Pdf/`): a pure `ReportPdfModel` builder (sections, rows, labels, already-formatted money) tested without QuestPDF, and a `ReportPdfDocument` that lays it out.
1. Header: logo, household name, period, generated-at, the buy/sell pair used (ADR-V019).
2. KPI row: total spend, budgeted, discretionary, unplanned (+ refundable), as on screen.
3. Charts: spend by class; income vs spend and income vs budget (single month with a rate); month-by-month bars (month mode); pace line (month mode); by bank, by card, by method (+ budget vs spent by method).
4. Table: category analysis by class with budgets, the Table view.
5. Appendix (optional): the CSV rows, date desc, repeating header, landscape not needed — narrow columns.
Strings: new `ReportPdfStrings.resx` (+ `.es`) in Api, alongside `EmailStrings` (Api cannot see `AppStrings`); covered by the localization parity gate.

**UI.** `Reports.razor`: **PDF** button beside Export CSV → dialog (appendix checkbox; currency + language shown) → **Download** via `IFileDownloadLauncher`. (**Email me** is added by REPORTS-8; the dialog is designed with the second action in mind.) Localized keys EN/ES.

**Tests (TDD).** Core: SVG builders (geometry unchanged — reuse the Ui test expectations; palette substitution; no `var(`). Api: `ReportPdfModel` builder per section incl. the no-rate degradations; appendix rows == `ExportAsync` rows for the same filters; endpoint returns `%PDF-`, page count ≥ 1, and PdfPig finds the period, a category name and an appendix payee; foreign household → 404. Ui: dialog renders, Download calls the launcher with the returned URL. E2E: one journey, PDF button → download event (like the CSV journey). QA plan: web + Android share sheet cases.

**Docs.** ADR-V022; `docs/stories/reports.md` REPORTS-7; `FEATURES.md` reports section; `DEPLOYMENT.md` if the Dockerfile gains a package; Postman; QA §reports.

**Not in scope.** Scheduled sends, past-reports list, custom sections, dark-theme PDF.

### Platform PR — email attachments *(upstream, `perezosoft-platform`)*

`IEmailSender.SendAsync(..., IReadOnlyList<EmailAttachment>? attachments = null)` with `EmailAttachment(FileName, Content, MediaType)`; `EmailOutboxPayload` gains `Attachments` (base64, same as inline images); `SmtpEmailSender` adds them through MailKit's `BodyBuilder.Attachments`; a size guard (reject > 10 MB total — Brevo's limit) with a clear `EmailSendException`. Tests: payload round-trip, MailKit message has the part, size guard. Then a `chore/sync-platform-NNN` PR here. **Gate:** discussed with the owner first per the platform-ownership rule — this plan is that discussion; confirm before opening the platform PR.

### REPORTS-8 — Email me this report *(owner request, 2026-09-16)*

**As a** household member **I want** the PDF I just configured sent to my own inbox **so that** the month's report is in my mail where I keep things.

**API.** `POST /api/reports/pdf/email` — same body as REPORTS-7; builds the same PDF, queues one outbox email to the **caller's** address with the PDF attached and a short branded body (`BrandedEmail.Report(...)`, EN/ES, period + one-line summary + "attached"), returns **202** with the period and file name. Quota: A9 daily cap → 429 `quota_exceeded` (existing error shape). No file stored (attachment travels in the outbox payload). Postman updated.

**UI.** The dialog's second action, **Email me**; success toast "Sent to {email}". Native shells: same, nothing platform-specific.

**Tests.** Api: outbox row has the attachment, correct recipient, subject localized; cap enforced; rate-unavailable behaves as REPORTS-7. Email template test (as the existing `BrandedEmail` tests). E2E: Mailpit receives a message with a PDF part. QA: web + one native.

**Docs.** `reports.md` REPORTS-8; `FEATURES.md`; Postman; QA; `.env.example` if the cap is configurable.

### INCOME-1 — Income lines: who earns what, how it is paid *(owner decision, 2026-09-16)*

**As a** household member **I want** each income listed as its own line — whose it is, its currency, whether it is fixed or an estimate, and how often it is paid — **so that** a weekly salary, a monthly salary and a variable side income all land correctly in the month's plan without typing four-week and five-week figures.

**Model** (`DATA_MODEL.md` first; both entities `ITenantScoped` with the RLS policy in the same migration, `IUserDataContributor`/tenant contributor wired):
- `IncomeLine`: `Name` (unique per household, case-insensitive), `MemberUserId?`, `Currency`, `Kind` (`fixed|variable`), `PayPeriod` (`weekly|biweekly|monthly`), `AmountPerPeriod`, `BiweeklyPayDays` (two day-of-month values, default 15 + last; only for biweekly), `IsActive`, `SortOrder`. Stored-value constants `IncomeKinds`, `PayPeriods` in Core with `.All`.
- `MonthIncome`: `MonthId`, `IncomeLineId?`, `Label`, `MemberUserId?`, `Currency`, `Amount` (editable), `PlannedAmount` (what the snapshot derived — so an edit is visible as such).
- Snapshot rule (`IncomeSnapshotService`, pure, Core): at month creation, one row per active line; `weekly` → amount × `WeekCount`; `monthly` → amount; `biweekly` → amount × count of pay days inside `[Week1StartDate, last week end]`. Variable lines snapshot their estimate and are meant to be edited.
- `IncomeCalculator` sums `MonthIncome` rows (+ inflows) → same `IncomeSummary`; **no dashboard/report shape change**.
- Removed: the six `BudgetSettings` income columns and the four `Month` income columns.

**Migration (data).** Per household: two lines "Primary income" / "Secondary income" (no member). 5w == 4w → `monthly` at that amount; 5w/5 == 4w/4 → `weekly` at 4w/4; otherwise `weekly` at 4w/4 and the household's settings page shows a one-time "check your income lines" notice (owner's household is the only real one — a one-minute check). Every existing month gets its two `MonthIncome` rows from its snapshot fields (label + amount + currency). Zero-amount slots produce no line.

**API.** `/api/incomes` (new unique prefix, R35): list / create / update / deactivate + reactivation 409 offer (ADR-V008 pattern, as expense lines) / `PUT /order`. `GET /api/months/{id}` returns `income_rows`; `PUT /api/months/{id}/income` takes the rows (amount per row; a row may be added ad hoc for that month only, `income_line_id` null). `budget-settings` loses the income fields. Postman updated.

**UI.** Settings → **Income** page (the expense-lines page pattern: ordered list, add/edit dialog with member picker from the roster, currency, fixed/variable, pay period + amount, biweekly days). Month page: income header becomes a list of rows, each editable, showing planned vs current when edited. Budget-settings page loses its income card.

**The form, field by field (owner-approved 2026-09-16).**

| Field | Options | Required | Meaning | In the month |
|---|---|---|---|---|
| Name | free text, unique per household, case-insensitive | yes | "Allan salary", "Freelance", "Apartment rent" | copied as the row label |
| Member | a roster member, or "Household" (none) | no | whose income; "Household" for shared income | copied; feeds INCOME-2 |
| Currency | ₡ / $ | yes | the currency the money arrives in, one per line | stored in it; other side projected at the day's rate |
| Kind | `fixed` / `variable` | yes | fixed = what arrives; variable = an estimate to correct | variable rows are expected to be edited; planned vs current shown when they differ |
| Pay period | `weekly` / `biweekly` / `monthly` | yes | how often the money arrives | weekly × week count; biweekly × pay days inside the window; monthly flat |
| Amount per period | positive, line currency | yes | one paycheck | multiplied per pay period |
| Pay days | two days of month, default 15 + last | biweekly only | when the quincena lands | counted against the month's dates |
| Active | on; deactivate, never delete | yes | inactive stops contributing to new months, keeps history | not snapshotted |
| Order | list position | no | display order | rows follow it |

Not on the form, on purpose: no 4w/5w amounts (the count comes from the month); no per-month values (the month page edits rows, and a one-off row can be added to a single month with no line); no dates for weekly or monthly lines.

**Week start is the payday, not a preference (owner, 2026-09-16).** The anchor rule is "last *week-start day* of the previous month", so the weekday decides the count: with Tuesday weeks September 2026 runs Aug 25 → Sep 28 (5 weeks, five transfers); with Monday weeks it would run Aug 31 → Sep 28 (4). For a weekly-paid household the week must start on the day the money moves so that week count = transfer count; the settings page and `budget-settings.md` must say this plainly.

**Edges (decided in planning).** Member leaves / is removed → their lines deactivate (income no longer arrives), month rows stay as history. Account erasure → per-user contributor nulls `MemberUserId` on lines and rows, amounts kept. Any member edits any line (household budget is shared).

**Tests.** Core: snapshot rule per period (weekly 4/5, monthly, biweekly 2/3 paydays incl. last-day-of-month), calculator on rows, migration mapping rules (pure function). Api: CRUD + uniqueness + 409 offer + order; month create snapshots rows; month income PUT; leave/remove deactivation; erasure nulling; RLS parity gate. Ui: income page + month rows. E2E: add a line, create a transaction, the month shows the derived plan. QA cases.

**Docs.** ADR-V023; `docs/stories/income.md`; `DATA_MODEL.md`; `FEATURES.md`; `budget-settings.md` amended; Postman; QA; `my-seed.sql` re-authored for the new columns (owner).

**Split.** INCOME-1 ships end-to-end without any new breakdown. **INCOME-2** adds "income by member" to the reports (analysis response + a donut) and turns the PDF's income block into a table with a member column. Small, additive, after INCOME-1.

## 5. Order and PRs

| Order | PR | Branch | Notes |
|---|---|---|---|
| 1 | REPORTS-7 PDF (spike commit + ADR-V022 + feature) | `feat/reports-pdf` | Blocks nothing; proves the engine on the Render image. |
| 2 | Platform: email attachments | `perezosoft-platform` `feat/email-attachments` | Owner confirms first (platform backend). Can start in parallel with 1. |
| 3 | Sync platform | `chore/sync-platform-NNN` | Mechanical. |
| 4 | REPORTS-8 Email me | `feat/reports-pdf-email` | Needs 1 + 3. |
| 5 | INCOME-1 income lines (+ ADR-V023) | `feat/income-lines` | Independent of 1–4; larger; schema + data migration. |
| 6 | INCOME-2 income by member | `feat/income-by-member` | Touches reports + PDF; after 1 and 5. |

Each PR: story first (Gherkin), failing tests, implementation, Postman, localization parity, QA-plan rows, docs. Commit/PR per `WAYS_OF_WORKING.md`; no git operations without the owner's go-ahead (C+P+PR).

## 6. Open items for the owner

1. Confirm A1–A10 (or edit them here).
2. Green-light the platform PR (attachments on `IEmailSender`).
3. Font: if Nunito lacks ₡, accept a fallback face for money cells only, or a different single face for the whole PDF?
4. Biweekly default pay days 15 + last — right for the households you expect?
