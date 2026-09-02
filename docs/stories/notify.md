# Stories — In-app notifications (`NOTIFY`)

> One file per epic. A **per-user** notification center (read/unread) + **per-user delivery preferences**
> (in-app / email), fanned out through the existing **outbox** (ADR-007) so one call reaches a user on
> the channels they chose. Design decision + constraints in **ADR-013**. Stories use Gherkin acceptance
> criteria. **Status: ✅ COMPLETE** — NOTIFY-1 (in-app center) + NOTIFY-2 (preferences + email fan-out).
> **UI shipped** (`feat/ui-3-notifications`): a header **bell** (`NotificationBell` — unread badge,
> dropdown list, mark-read / mark-all-read) and a **Notifications** preferences card in Settings
> (`NotificationPrefsCard` — in-app/email switches). EN/ES; QA-NOTIF-01..04.
> **Extended 2026-07-09 (QA pass):** caller-scoped **deletion** — `DELETE /api/notifications/{id}` and
> `DELETE /api/notifications` (`?read=true` = only already-read) — surfaced in the bell as a per-row
> trash icon + footer **Clear read** / **Clear all**, so downstream apps can let users clear
> notifications without touching the platform or the DB.

**Epic key:** `NOTIFY`

**Prerequisites (external, before any code):**
- None. Reuses the outbox-backed `IEmailSender` (ADR-007 ✅), the per-user pattern (ADR-C2), and account
  erasure (GDPR-2). No new packages.

**Per-user, not tenant-scoped (ADR-C2/ADR-013):** notifications + preferences are keyed by `user_id`
and scoped to the authenticated caller (`NameIdentifier`) — never the tenant filter, never another
user's data. Wiped by account erasure (GDPR-2).

---

### NOTIFY-1 — In-app notification center

**Status: ✅ Implemented** (`feat/notify-1-center`). `Notification` (per-user: `Kind`/`Title`/`Body`/
`Metadata` jsonb/`ReadAt`/`CreatedAt`; migration `AddNotifications`). `NotificationService`:
`NotifyAsync` **stages** the in-app row on the caller's unit of work (transactional, like `IAuditLog`);
`ListAsync` (newest-first, `before` cursor, ≤100), `UnreadCountAsync`, `MarkReadAsync` (own only),
`MarkAllReadAsync`, `DeleteAsync` (own only), `DeleteAllAsync` (all or only-read). User-scoped
`NotificationsController` (`GET /api/notifications`, `/unread-count`, `POST /{id}/read`, `/read-all`,
`DELETE /{id}`, `DELETE /api/notifications` + `?read=true`) — scoped to the `NameIdentifier` claim.
Account erasure (GDPR-2) wipes notifications. Tests
`tests/Api.Tests/Notify/NotificationServiceTests.cs` (list/newest-first/paginate, unread count, mark
one/all, delete one/bulk + read-only sweep, per-user isolation, metadata-as-json).

**As a** user
**I want** an in-app list of notifications with read/unread state
**So that** I can see what happened without relying on email

**Context / notes:** `Notification` (per-user: `user_id`, `kind`, `title`, `body`, `metadata` jsonb,
`read_at`, `created_at`). `INotificationService.NotifyAsync(userId, kind, title, body, metadata)` creates
the in-app row **transactionally** (in-app only for this slice; the email channel is NOTIFY-2). A
user-scoped center API: list (paginated, newest first), unread count, mark-one-read, mark-all-read — all
scoped to the caller. **Wiped by account erasure** (GDPR-2). No secrets in `metadata`.

**Acceptance criteria**

```gherkin
Scenario: A notification appears in my center
  Given a feature calls NotifyAsync for me
  When I list my notifications
  Then the new one is there, unread, newest first

Scenario: Read state
  Given I have unread notifications
  When I mark one (or all) read
  Then its read_at is set and my unread count drops

Scenario: Notifications are per-user
  Given two users each have notifications
  When I list or mark read
  Then I only ever see/affect my own — never another user's

Scenario: Erasing my account removes my notifications
  Given I have notifications
  When I delete my account (GDPR-2)
  Then they are wiped
```

**Out of scope:** preferences + email fan-out (NOTIFY-2); realtime push/SignalR (a later concern —
polling is fine at platform scale); notification templates/i18n beyond a title+body.
**Definition of done:** tests first; create + list (newest-first, paginated), unread count, mark
one/all read, per-user isolation, erasure wipes notifications; merged, app working; ADR-013 referenced.

---

### NOTIFY-2 — Delivery preferences + email fan-out

**Status: ✅ Implemented** (`feat/notify-2-preferences`). `NotificationPreference` (per-user unique,
in-app/email, **default on**; migration `AddNotificationPreferences`). `NotifyAsync` is now the
**fan-out**: reads prefs → stages the in-app row when in-app is on, and sends the email via the
**outbox-backed `IEmailSender`** (ADR-007) when email is on — resolving the user's email, channels never
hard-coded. `GetPreferencesAsync` (defaults on) + `SetPreferencesAsync` (upsert); endpoints
`GET|PUT /api/notifications/preferences`. Account erasure (GDPR-2) wipes preferences. Tests
`tests/Api.Tests/Notify/NotificationFanOutTests.cs` (default→both channels, email-off→in-app-only,
in-app-off→email-only, defaults-on, upsert) via a capturing email sender.

> **2026-07-15 — non-suppressible security alerts (v3 audit ADM-1).** `NotifyAsync` used to let a user
> silence ANY notification via prefs — including a staff **MFA reset** (`security.mfa_reset`), an
> account-takeover primitive, so "a malicious reset cannot be silent" was false. Kinds in the
> **`security.`** namespace (`NotificationKinds.IsSecurity`) now **bypass prefs** and force BOTH channels —
> the out-of-band email is the point (an attacker inside the account can't turn it off). Everything else
> still honors prefs. Self-extending: any future `security.*` (password/email change, new-device sign-in)
> inherits it. Tests: `NotificationFanOutTests` (`SecurityKind_WithBothChannelsOff…`,
> `NonSecurityKind_WithBothChannelsOff_IsFullySuppressed`).

**As a** user
**I want** to choose whether I'm notified in-app and/or by email
**So that** I control how I'm reached

**Context / notes:** `NotificationPreference` (per-user channel toggles: in-app / email; default on).
`NotifyAsync` becomes the **fan-out**: always considers prefs — in-app row when in-app is on (transactional),
and an **email** via the outbox-backed `IEmailSender` (ADR-007) when email is on. Get/update-preferences
endpoints (user-scoped). A feature calls `NotifyAsync` once; channels are never hard-coded at the call site.

**Acceptance criteria**

```gherkin
Scenario: Fan-out honors preferences
  Given my preferences: in-app on, email off
  When NotifyAsync runs for me
  Then an in-app notification is created and no email is sent
  And with email on, an email is enqueued via the outbox too

Scenario: Defaults
  Given I never set preferences
  Then both channels default to on

Scenario: Update my preferences
  When I update my notification preferences
  Then subsequent NotifyAsync calls respect them

Scenario: Email goes through the reliable path
  Given email is on
  When NotifyAsync sends
  Then it uses the outbox-backed IEmailSender (retried), not an inline send
```

**Out of scope:** per-notification-kind granularity (a single global channel toggle is enough for the
platform — extendable later); SMS/push channels; digest/batching.
**Definition of done:** tests first; prefs default-on, get/update, fan-out respects prefs (in-app +
email-via-outbox), email uses the outbox sender; merged, app working; ADR-013 referenced.

---

## Slice plan (implementation map)

Ordered, each a mergeable vertical slice. TDD throughout.

1. ✅ **In-app center (NOTIFY-1).** — DONE. `Notification` (per-user) + migration `AddNotifications`;
   `NotificationService.NotifyAsync` (staged in-app insert) + user-scoped center API
   (list/unread-count/mark-read/mark-all); account erasure wipes notifications.
2. ✅ **Preferences + fan-out (NOTIFY-2).** — DONE. `NotificationPreference` (per-user, default-on;
   migration `AddNotificationPreferences`) + `GET|PUT /api/notifications/preferences`; `NotifyAsync`
   fans out to in-app + email (outbox-backed `IEmailSender`) per prefs; erasure wipes prefs.

**Known sharp edges (from ADR-013):** everything is **per-user** (scoped to the caller, never
cross-user); **in-app = transactional DB row, email = outbox** (don't mix them up); **preferences gate
delivery** (no hard-coded channels); notifications are **user PII** (erasure wipes them); **no
secrets/PII** in `metadata`.
