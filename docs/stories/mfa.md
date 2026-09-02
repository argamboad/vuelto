# Stories — MFA (authenticator-app TOTP)

> One file per epic. Optional **authenticator-app TOTP** second factor, enforced as a **step-up** after
> the existing primary auth (ADR-002). Reuses Data Protection (encrypt the secret + sign the challenge)
> and `ITokenHasher` (recovery codes); only Otp.NET is new. Design decision + constraints in **ADR-012**.
> Stories use Gherkin acceptance criteria. **Status: ✅ COMPLETE** — MFA-1 (enrollment) + MFA-2 (login
> step-up on the JSON paths) + **MFA-3** (step-up on the OAuth/magic-link **redirect** paths — the
> security fix). **UI shipped** (`feat/ui-2-mfa`): a Two-factor card in Settings
> (enroll → client-side QR of the `otpauth://` URI + manual key → confirm → one-time recovery codes →
> disable) and the OTP sign-in **step-up** prompt on Login (EN/ES; QA-MFA-01..03). **MFA-3**
> (`feat/mfa-3-redirect-stepup`): OAuth callback + magic-link now route through
> `CompleteOrChallengeAsync` and redirect to `/login?mfa=<challenge>`, reusing the same prompt
> (QA-MFA-04). **MFA-4** (`feat/mfa-4-native-stepup`): the native (MAUI) client now handles the
> `{mfa_required, challenge}` response on the OTP + OAuth-exchange paths (`AuthService.VerifyOtpAsync`/
> `SignInWithOAuthAsync` return a `SignInResult`; `VerifyMfaAsync` completes it) and reuses the same
> step-up prompt (QA-MFA-05). **MFA is now enforced on every sign-in path, web and native — no gaps.**
> **Extended 2026-07-10 (ADR-021 addendum, `feat/admin-mfa-reset`):** a user who loses **both** the
> authenticator and the recovery codes now has a recovery path — a **staff-only reset**
> (`DELETE /api/admin/users/{userId}/mfa` → `IMfaService.ResetAsync`, no code required) after
> out-of-band identity verification; audited in the target's tenant (`admin.mfa.reset`) + the user is
> notified (in-app + email). Second factor only — primary auth untouched; re-enroll from Settings.
> See `docs/stories/admin.md` + ADR-021 addendum. QA-ADMIN-07.

**Epic key:** `MFA`

**Prerequisites (external, before any code):**
- None to build/run. Reuses the custom auth stack, Data Protection (✅ wired), and `ITokenHasher` (✅).
- Packages (latest stable, no previews — ADR-C10): **`Otp.NET`** (TOTP math + secret/URI helpers).

**Reuses:** `SessionService.IssueAsync` (the login convergence point), `IDataProtector` (secret at rest
+ challenge signing), `ITokenHasher` (recovery-code hashing), and the account-erasure identity wipe
(GDPR-2 — MFA rows are user PII).

---

### MFA-1 — Enrollment & management (TOTP secret, recovery codes)

**Status: ✅ Implemented** (`feat/mfa-1-enrollment`). Entities `UserMfa` (encrypted secret, `Enabled`)
+ `MfaRecoveryCode` (hashed, single-use) — migration `AddMfa`. `MfaService` (`src/Api/Services/`,
Otp.NET): begin (secret + `otpauth://` URI, not enabled), confirm (valid code → enable + 10 hashed
recovery codes returned once), disable (valid code → wipe), status, and `VerifyAsync` (TOTP or a
single-use recovery code) for the MFA-2 step-up. Secret encrypted via `IDataProtector`; recovery codes
hashed via `ITokenHasher` over a canonical form — the codes are short, human-typeable `xxxxx-xxxxx`
(unambiguous alphabet, case/hyphen/space-insensitive entry; **fixed 2026-07-09** — they were 88-char
opaque tokens that overflowed the maxlength-14 inputs and could never be entered). Endpoints
`GET|POST /api/auth/mfa[/enroll|/confirm|/disable]`. Account
erasure (GDPR-2) extended to wipe both tables. Tests `tests/Api.Tests/Mfa/MfaServiceTests.cs`
(enroll/confirm/verify/recovery-single-use/disable + secret-encrypted + hashed codes).

**As a** user
**I want** to enable an authenticator app as a second factor
**So that** my account is protected even if my primary credential leaks

**Context / notes:** `UserMfa` (`UserId`, `EncryptedSecret`, `Enabled`, `EnrolledAt`) — secret
**encrypted at rest** via `IDataProtector`; `MfaRecoveryCode` (`UserId`, `CodeHash`, `UsedAt`) — hashed
with `ITokenHasher`, single-use. `IMfaService`: begin enrollment (generate secret → return the
`otpauth://` provisioning URI; **not yet enabled**), confirm (verify a TOTP code → **enable** + issue +
return recovery codes **once**), disable (verify a code → wipe secret + codes), status, and a
`VerifyAsync(userId, code)` used by the login step-up (MFA-2). Endpoints under `/api/auth/mfa/*`
(authenticated). **Also extends account erasure (GDPR-2)** to wipe `UserMfa` + `MfaRecoveryCode`.

> **2026-07-15 — brute-force cap (v3 audit ADM-3).** The step-up verify had no per-user attempt cap —
> only a per-IP limiter — and a wrong code doesn't consume the challenge, so an attacker holding factor 1
> could mint a fresh challenge per guess and spray TOTPs across IPs (~333k expected guesses). `VerifyAsync`
> now tracks **per-user consecutive failures** on `UserMfa` (`FailedAttemptCount` + `LockedUntil`, migration
> `MfaLockout`): the increment is atomic (conditional `ExecuteUpdate`, cap on the persisted value), a
> success resets it, and once `Auth:Mfa:MaxAttempts` (default 5) is hit the user's step-up is locked for
> `Auth:Mfa:LockoutWindowMinutes` (default 15). Recovery codes share the path (also capped). The endpoint
> keeps its single generic 401 (`mfa_failed`) — no lockout oracle. Tests: `MfaLoginServiceTests`
> (`Verify_CapWrongCodesAcrossFreshChallenges…`, `Lockout_LiftsAfterTheWindow`, `Success_ResetsTheFailureCounter`).

> **2026-07-15 — claim-before-verify (v3 audit LB-AUTH-1).** `VerifyChallengeAsync` used to call
> `MfaService.VerifyAsync` (which **burns** a recovery code / advances the anti-replay step) *before*
> `challenges.Consume`, so a claim that then failed — a replayed challenge, or one evicted from the
> `IMemoryCache` — spent the second factor with **no session issued** (the user's recovery code simply
> gone). The challenge is now claimed **first**; only the winner ever touches the factor. A wrong code burns
> nothing, so the claim is handed back (`IMfaChallengeService.Restore`) and the same step-up stays
> retryable — the per-user lockout above, not the challenge, is what caps guessing. The signed token's own
> expiry still caps the real lifetime, so a restore can't extend a challenge.

> **2026-07-15 — peppered recovery-code hashing (v3 audit ADM-4).** Typeable recovery codes are only ~49.5
> bits, and were stored under the shared **unsalted SHA-256** `ITokenHasher` — a leaked `MfaRecoveryCode`
> row was offline-crackable (day-scale on a GPU) into a working second factor. They now hash through a new
> **`IRecoveryCodeHasher`** — HMAC-SHA256 under a server **pepper** — so a DB leak alone is useless (an
> attacker needs `Jwt:Secret` too). The pepper is **derived from `Jwt:Secret` via HKDF** with a
> domain-separation label, so it needs no separate secret and inherits that secret's presence/min-length
> guard while staying independent of JWT signing. HMAC is deterministic, so the by-hash lookup is unchanged;
> codes stay 10 chars (no UI change). The high-entropy tokens (magic-link/API-key/invitation) keep plain
> SHA-256 — the finding is specific to the low-entropy codes. **Upgrade note:** changing the hash invalidates
> existing recovery-code hashes; a real deployment adopting this must have users regenerate codes (disable +
> re-enroll) — no dual-read fallback (this template has no real users). Tests: `RecoveryCodeHasherTests`.

**Acceptance criteria**

```gherkin
Scenario: Enroll and enable TOTP
  Given I am signed in without MFA
  When I begin enrollment
  Then I receive an otpauth:// provisioning URI (secret never returned in plaintext later)
  And when I confirm with a valid code from my authenticator
  Then MFA is enabled and I receive a set of one-time recovery codes

Scenario: Enabling requires proving possession
  Given I began enrollment
  When I confirm with an invalid code
  Then MFA is not enabled

Scenario: Recovery codes are single-use and hashed
  Given MFA is enabled with recovery codes
  Then the codes are stored only as hashes
  And a code works exactly once

Scenario: Disable requires a valid code
  Given MFA is enabled
  When I disable it with a valid code
  Then the secret and recovery codes are removed and MFA is off

Scenario: Erasing my account removes MFA data
  Given MFA is enabled
  When I delete my account (GDPR-2)
  Then my UserMfa and recovery codes are wiped
```

**Out of scope:** login enforcement (MFA-2); SMS/email OTP as a factor (this is authenticator TOTP);
WebAuthn/passkeys (a separate future epic); per-tenant "require MFA" policy.
**Definition of done:** tests first; secret encrypted (never round-tripped in plaintext), confirm
enables only on a valid code, recovery codes hashed + single-use, disable wipes, erasure wipes MFA;
merged, app working; ADR-012 referenced.

---

### MFA-2 — Login step-up enforcement

**Status: ✅ Implemented (JSON login paths)** (`feat/mfa-2-login-stepup`). `MfaChallengeService` mints/reads
a **signed, 5-min** challenge (Data Protection time-limited) binding user + provider + native;
`MfaLoginService.CompleteOrChallengeAsync` sits at the `SessionService.IssueAsync` point — MFA-off →
session as before; **MFA-on → a challenge, no session**. `POST /api/auth/mfa/verify` (`VerifyChallengeAsync`)
checks the challenge + a TOTP/recovery code and issues the session (cookie for browser / body for native,
from the challenge's native flag); any bad/expired challenge or wrong code is a single **401**. Wired
into the **JSON login paths** — OTP verify + native exchange. Tests `tests/Api.Tests/Mfa/`
(`MfaChallengeServiceTests` round-trip/tamper/foreign-signer; `MfaLoginServiceTests` challenge-vs-session,
valid/​wrong-code, tampered-challenge, native-flag preserved).

> **Follow-up (flagged in ADR-012):** the **redirect** login paths — OAuth callback + magic-link verify —
> are **not yet gated**; enforcing them means redirecting the browser to a client `/mfa?challenge=…` page,
> which needs the (not-yet-built) MFA **UI**. Tracked as a fast-follow. Since the platform ships no MFA
> enrollment UI, no platform-web user can have MFA on via those paths today.

**As a** user with MFA enabled
**I want** to be asked for a code after my primary sign-in
**So that** a leaked primary credential alone can't access my account

**Context / notes:** at the `SessionService.IssueAsync` convergence, when the user has MFA enabled,
primary auth returns an **MFA challenge** — a short-lived **signed** token (Data Protection
time-limited) naming the user — instead of a full session. `POST /api/auth/mfa/verify` accepts the
challenge + a TOTP **or recovery** code and, on success, issues the real session (`IssueAsync`). Wired
into the JSON login paths (OTP/magic-link verify, native exchange); the OAuth **redirect** path routes
the browser to an MFA prompt carrying the challenge. Users **without** MFA are unaffected.

**Acceptance criteria**

```gherkin
Scenario: MFA-enabled login requires the second factor
  Given I have MFA enabled
  When I complete primary auth
  Then I do not get a session yet — I get a short-lived MFA challenge
  And only after POSTing a valid TOTP (or recovery) code with the challenge do I get the session

Scenario: The challenge is required and time-limited
  Given a login awaiting MFA
  When the challenge is missing, expired, tampered, or the code is wrong
  Then no session is issued (401)

Scenario: Users without MFA are unaffected
  Given I have no MFA
  When I complete primary auth
  Then I get a session directly, as before

Scenario: A recovery code completes the challenge once
  Given I lost my authenticator
  When I submit a valid recovery code with the challenge
  Then I get a session and that recovery code is consumed
```

**Out of scope:** "remember this device" / trusted devices; step-up for sensitive actions beyond login;
admin-forced MFA reset *(since shipped — ADR-021 addendum 2026-07-10, staff-only `DELETE
/api/admin/users/{userId}/mfa`; see `docs/stories/admin.md`)*.
**Definition of done:** tests first; challenge issued when MFA on, session withheld until verified,
expired/tampered/wrong-code rejected, no-MFA path unchanged, recovery-code path consumes one; merged,
app working; ADR-012 referenced.

---

## Slice plan (implementation map)

Ordered, each a mergeable vertical slice. TDD throughout.

1. ✅ **Enrollment & management (MFA-1).** — DONE. `UserMfa` + `MfaRecoveryCode` (migration `AddMfa`);
   `MfaService` (begin/confirm/disable/status/verify) with Otp.NET + `IDataProtector` secret +
   `ITokenHasher` recovery codes; `/api/auth/mfa/*` endpoints; account erasure (GDPR-2) wipes MFA rows.
2. ✅ **Login step-up (MFA-2).** — DONE (JSON paths). `MfaChallengeService` (signed, 5-min) +
   `MfaLoginService` at the `IssueAsync` convergence; `POST /api/auth/mfa/verify` completes the session;
   wired into OTP verify + native exchange; no-MFA unaffected. **Follow-up:** redirect-path (OAuth/
   magic-link) step-up needs the MFA client page.

**Known sharp edges (from ADR-012):** the secret stays **encrypted at rest** (never re-returned/logged);
recovery codes are **hashed + single-use**; **enable/disable require a valid code** (prove possession —
the one exception is the **staff reset**, ADR-021 addendum: admin-path only, out-of-band identity check,
audited + user notified); the **step-up is server-enforced** (signed challenge required); MFA is
**user PII** (wiped by erasure).
