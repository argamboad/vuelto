# E2E tests (Playwright / NUnit)

End-to-end tests that drive a real browser against the running Web app + API, reading OTP
codes from **Mailpit** (the same flow a manual tester uses). They cover the QA plan's web
smoke path that doesn't need an external OAuth provider.

## Prerequisites

1. **Backing services** — `docker compose up -d` (Postgres + Mailpit).
2. **API — must send email to Mailpit, not a real provider.** The dev default
   (`appsettings.Development.json`) already points at Mailpit (`localhost:1025`). **If your
   repo-root `.env` overrides `Email__Smtp__*` to a real SMTP provider (e.g. Brevo), override
   it back for the test run** via command-line args (command-line config beats `.env`, and
   leaves `.env` untouched):
   ```sh
   dotnet run --project src/Api --launch-profile https -- \
     --Email:Smtp:Host=localhost --Email:Smtp:Port=1025 --Email:Smtp:Username= --Email:Smtp:Password= \
     --Auth:RateLimit:PasswordlessPermitLimit=1000 \
     --Admin:StaffEmails:0=e2e-staff@example.com \
     --Billing:Stripe:SecretKey=
   ```
   The `Admin:StaffEmails` entry enables the admin-console journey (ADMIN-3); it must match
   `AnnouncementJourneyTests.StaffEmail`. The empty `Billing:Stripe:SecretKey` forces the
   **FakeBillingProvider** even if your `.env` has Stripe test keys — the billing journey
   (BILLING-8) depends on the fake's deterministic checkout URLs and `valid` webhook signature.
   CI sets the same overrides. Tests that call the API directly (the billing webhook) use
   `E2E_API_BASE_URL` (default `https://localhost:7160`; CI overrides).
   If your `.env` doesn't override email, you can drop the `--Email:*` args — but keep the
   **rate-limit override**: the journey tests sign in several users per run from one IP, which
   trips the production default (5 OTP requests/min/IP → 429 → flaky "no OTP email" timeouts).
   CI sets the same override for its E2E job.
3. **Web** — `dotnet run --project src/Web --launch-profile https` (serves <https://localhost:7008>).
4. **Browser (once)** — `pwsh tests/E2E.Tests/bin/Debug/net10.0/playwright.ps1 install chromium`.

## Run

```sh
dotnet test tests/E2E.Tests
```

Base URL defaults to `https://localhost:7008`; override with `PLAYWRIGHT_BASE_URL`. The
dev self-signed cert is accepted (`IgnoreHTTPSErrors`).

## Coverage

| Test | QA plan |
|------|---------|
| Login page renders | — |
| Email OTP sign-in → lands in the app shell | QA-SMK-01 |
| Sign out → back to login | QA-SMK-03 |
| Invalid email rejected before the code step | QA-AUTH-09 |
| Owner invites; member joins via token (two contexts) | QA-INV-01, QA-INV-02 |
| Owner promotes and demotes a member | QA-HH-09, QA-HH-10 |
| Roster is permission-aware (member + admin views) | QA-HH-02, QA-HH-11, QA-HH-12 |
| Owner removes a member (confirm dialog) | QA-HH-03 |
| Inviting past the free-plan seat limit shows the upgrade prompt | QA-HH-14 |
| Revoking a pending invitation frees the seat | QA-INV-08 |
| Notification bell shows the empty state for a fresh user | QA-NOTIF-01 (empty state) |
| Delivery preferences round-trip + per-user isolation | QA-NOTIF-03 |
| Magic-link sign-in → lands in the app shell | QA-AUTH-01 |
| Used magic link rejected (single-use) | QA-AUTH-05 |
| Owner transfers ownership (badges swap, controls follow) | QA-HH-05 |
| Member leaves → re-homed to a fresh tenant-of-one | QA-HH-08 |
| Sole owner dissolves → re-homed, old household gone | QA-HH-07 |
| Member deletes their account → signed out, off the roster | QA-SET-07 |
| Staff announcement → member's bell badge + item; mark-read clears | QA-ADMIN-04, QA-NOTIF-01, QA-NOTIF-02 |
| Non-staff gets the admin-console forbidden state | QA-ADMIN-01 (partial) |
| Billing page: free → upgrade → webhook → pro (fake provider) | QA-BILL-02 |
| Billing page is owner-only (member sees the pointer state) | QA-BILL-01 |
| Member joins by pasting the invite code on /join (NATIVE-4b) | QA-INV-02 (code-entry variant) |
| Invalid pasted code → inline error, form stays usable | — |
| Owner requests the GDPR export → real browser download (.json) | QA-HH export case |

OAuth (Google/Microsoft), desktop, and Android are intentionally **not** automated here —
they need external provider accounts / native runners. See `docs/QA_TEST_PLAN.md` for that
manual coverage. Selectors use stable `data-testid` hooks on the shared UI components.
