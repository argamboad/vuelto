// Native smoke — Android (NATIVE-7, ADR-018). Mirrors tests/E2E.Tests/NativeSmokeTests.cs
// (the Windows leg) but in Node: Android WebView's CDP doesn't support the browser-context
// management Playwright's ConnectOverCDPAsync needs, so this leg uses playwright-core's
// _android module instead (adb + WebView attach — the purpose-built path, Node-only).
//
// Prereqs (the CI job or a local rehearsal provides them): an emulator/device with the DEBUG
// app installed and launched (EmbedAssembliesIntoApk=true — a fast-deployment APK won't start
// from a plain `adb install`), `adb reverse` on the API's local http port (the MAUI csproj does it),
// the API on that port, Mailpit on MAILPIT_BASE_URL (default: this repo's Mailpit UI port, local-ports.props).

const { execFileSync } = require('child_process');
const PKG = process.env.NATIVE_SMOKE_PKG || 'com.perezosoft.vuelto';
const MAILPIT = process.env.MAILPIT_BASE_URL || `http://localhost:${localPort('LocalMailUiPort')}`;

// The repo's one source of local ports (Arch A10): read, not restated.
function localPort(name) {
  const props = require('fs').readFileSync(require('path').join(__dirname, '..', '..', 'local-ports.props'), 'utf8');
  return props.match(new RegExp(`<${name}>(\\d+)</${name}>`))[1];
}

const stamp = () => new Date().toISOString();

// A device handle, fresh each time. Run 35024411715 lost the whole adb connection mid-journey: the
// WebView target closed, the separately-recorded `adb logcat` ended in the same second, and the retry's
// first `device.shell` threw "Device is closed" — Playwright never hands a closed AndroidDevice back to
// life, so a retry on the old handle cannot succeed. Every attempt asks for a new one, restarting the
// adb server if the old one is gone, within a deadline like every other wait here.
//
// omitDriverInstall: the smoke drives the WebView over CDP and never uses Playwright's on-device driver
// (taps and fills on native widgets). Installing it is a package change on the device, and the last
// line logcat recorded on that run was logd re-reading the package list.
async function acquireDevice(timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    try {
      const { _android } = require('playwright-core'); // here, not at the top: the unit tests load this file without it
      const devices = await _android.devices({ omitDriverInstall: true });
      if (devices.length > 0) return devices[0];
      console.error(`${stamp()} no adb device listed yet`);
    } catch (e) {
      console.error(`${stamp()} adb server unreachable (${e.message.split('\n')[0]}); starting it`);
      try { execFileSync('adb', ['start-server'], { timeout: 30_000, stdio: 'inherit' }); } catch { /* retried below */ }
    }
    if (Date.now() >= deadline) throw new Error(`no adb device within ${timeoutMs / 1000}s`);
    await new Promise(r => setTimeout(r, 3000));
  }
}

async function mailpit(path, init) {
  const res = await fetch(`${MAILPIT}${path}`, init);
  if (!res.ok) throw new Error(`Mailpit ${path} -> ${res.status}`);
  return res;
}

// Same matching rules as the C# Mailpit helper: OTP subject + a standalone 6-digit code.
async function waitForOtp(toEmail, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const list = await (await mailpit('/api/v1/messages?limit=50')).json();
    const summary = (list.messages ?? []).find(m =>
      (m.To ?? []).some(a => a.Address?.toLowerCase() === toEmail.toLowerCase()) &&
      (m.Subject ?? '').toLowerCase().includes('verification code'));
    if (summary) {
      const detail = await (await mailpit(`/api/v1/message/${summary.ID}`)).json();
      const match = `${detail.Text} ${detail.HTML}`.match(/(?<!\d)(\d{6})(?!\d)/);
      if (match) return match[1];
    }
    await new Promise(r => setTimeout(r, 500));
  }
  throw new Error(`No OTP email for ${toEmail} within ${timeoutMs / 1000}s`);
}

// Boot: attaching to the app's WebView proves the app process started (a G7-style crash dies
// here), and the visible login box proves Blazor booted inside it.
async function bootToLogin(device, attempt) {
  const webView = await device.webView({ pkg: PKG }, { timeout: 60_000 });
  const page = await webView.page();
  console.log(`connected (attempt ${attempt}): ${page.url()}`);

  // Unhandled .NET exceptions in the Blazor WebView only show up as console errors; without
  // this the smoke just times out waiting for UI and the root cause lives in logcat noise.
  page.on('console', msg => {
    if (msg.type() === 'error' || msg.type() === 'warning') console.error(`[webview ${msg.type()}] ${msg.text()}`);
  });
  page.on('pageerror', e => console.error(`[webview pageerror] ${e.message}`));

  const emailBox = page.getByTestId('login-email');
  await emailBox.waitFor({ state: 'visible', timeout: 60_000 });
  return { page, emailBox };
}

// The WHOLE journey is retried once, not just the boot wait. MAUI's BlazorWebView is torn down and
// re-created when Android recreates the Activity early in startup ("Cannot access a disposed
// object: 'IServiceProvider'" at WebViewManager.AttachToPageAsync; first seen on run 32769356890,
// where the same APK passed twice that morning) — the WebView the smoke attached to then goes away
// underneath it. Any failure BEFORE the login box is visible still retries, as it always did. Until
// 2026-09-15 only the boot wait retried, so the race was
// survivable ONLY while it landed before the login box rendered; when it lands after (run
// 34983821740: "connected", login box seen, then `fill` found no element 30 s later) the run failed
// on a healthy app. A journey-wide retry covers both landings; a real startup fault (the G7 class
// this canary exists for) still fails BOTH attempts.
const ATTEMPTS = 2;

// The shapes the teardown takes, none of which an app fault produces twice in a row: the attached
// target is gone, its execution context died, or the DOM the smoke was mid-way through is empty.
function looksLikeWebViewReplaced(message) {
  return [
    'Target page, context or browser has been closed',
    'Target closed',
    'Execution context was destroyed',
    'Device is closed',
    "waiting for getByTestId('login-email')",
  ].some(s => message.includes(s));
}

// One authorized page, reached IN-APP. Returns how it got there ('in-app' | 'fallback'). The fallback is a
// full reload, once — and it is LOUD: a broken Household link or hamburger used to leave the run green with
// one console.error nobody reads, so it now raises a workflow warning annotation (v4 audit T53).
async function navigateToHousehold(page, timeout = 60_000) {
  const household = page.getByTestId('household-rename-input');
  try {
    // Different from the platform: here Household sits inside the user menu, so the path is menu, then link.
    // On a phone-width window the whole header (the user menu included) collapses behind the hamburger —
    // Forgejo run 6 waited 60 s on an invisible `user-menu`, then the goto fallback hit the reload race.
    // Open the hamburger first when the menu isn't showing; on a wide window it already is.
    const userMenu = page.getByTestId('user-menu');
    if (!(await userMenu.isVisible())) await page.locator('button.navbar-toggler').click({ timeout });
    await userMenu.click({ timeout });
    await page.getByTestId('nav-household').click({ timeout });
    await household.waitFor({ state: 'visible', timeout });
    return 'in-app';
  } catch (e) {
    console.log(`::warning title=native smoke (android)::in-app navigation to Household failed (${e.message}); the run continued through a full page load. The user menu, its Household link or the hamburger is broken, or the emulator was too slow.`);
    await page.goto('https://0.0.0.1/household');
    await household.waitFor({ state: 'visible', timeout });
    return 'fallback';
  }
}

async function journey(device, attempt, seen) {
  const { page, emailBox } = await bootToLogin(device, attempt);
  seen.page = page;
  seen.booted = true;

  // OTP sign-in end-to-end through the real API + Mailpit (the native body-token transport).
  // The address is per-ATTEMPT: a retry must not read the first attempt's code back out of Mailpit.
  const email = `native-smoke-${Date.now()}@example.com`;
  await mailpit('/api/v1/messages', { method: 'DELETE' });
  await emailBox.fill(email);
  await page.getByTestId('login-send-otp').click({ timeout: 60_000 });
  const code = await waitForOtp(email, 60_000);
  await page.getByTestId('login-otp-code').fill(code);
  // Both buttons are disabled while the page is busy (disabled="@_busy"); on a cold emulator the
  // send round-trip + Blazor re-render can outlast Playwright's default 30 s click wait even though
  // the OTP mail is already in Mailpit (run 34005419602: "waiting for element to be … enabled" on
  // Verify code, green on re-run). Give the clicks the same 60 s every other wait here already has.
  await page.getByTestId('login-verify-otp').click({ timeout: 60_000 });
  // Attached, not visible: the responsive header collapses sign-out behind the hamburger on
  // a phone-sized window (same reasoning as the Windows leg).
  await page.getByTestId('sign-out').first().waitFor({ state: 'attached', timeout: 60_000 });

  // One authorized page: Household loads its data — proves the native Bearer path. Navigate IN-APP (click the
  // header link) instead of page.goto: a goto reloads the whole WebView, and on a cold emulator that reload
  // races the Blazor attach — a sibling app (vuelto run 35250560206) failed twice at exactly that step ("Cannot access a disposed object:
  // 'IServiceProvider'", then "There is no browser renderer with ID 3") on an APK whose own develop run had
  // passed, while the Windows smoke stayed green. Client-side navigation exercises the same authorized API call
  // without restarting the host. The goto stays as a fallback, once, if the link isn't reachable.
  await navigateToHousehold(page);
  const members = await page.getByTestId('member-row').count();
  if (members !== 1) throw new Error(`expected 1 roster row for a fresh owner, saw ${members}`);
  return page;
}

// What the page looked like when it failed — a closed target and a live-but-empty one are different
// diagnoses, and CI's `adb logcat -d` after the fact has come back empty (run 34983821740).
async function describeFailure(page) {
  if (!page) return 'no page was attached';
  if (page.isClosed()) return 'the attached WebView target was CLOSED';
  try {
    const seen = await page.evaluate(() => ({ url: location.href, chars: document.body.innerText.trim().length }));
    return `the page is open at ${seen.url} with ${seen.chars} characters of text`;
  } catch (e) {
    return `the page is open but unreachable: ${e.message.split('\n')[0]}`;
  }
}

// Whether the adb side is still there, asked of adb itself rather than of Playwright's handle — the two
// failures this smoke has seen differ exactly here (a replaced WebView keeps the device; a dropped
// connection loses both).
function adbDevices() {
  try {
    return execFileSync('adb', ['devices'], { timeout: 15_000, encoding: 'utf8' }).trim().replace(/\s*\n\s*/g, ' | ');
  } catch (e) {
    return `adb devices failed: ${e.message.split('\n')[0]}`;
  }
}

async function main() {
  let device;
  for (let attempt = 1; ; attempt++) {
    device = await acquireDevice(90_000);
    console.log(`${stamp()} device (attempt ${attempt}): ${device.serial()}`);
    if (attempt > 1) {
      await device.shell(`am force-stop ${PKG}`);
      await new Promise(r => setTimeout(r, 2000));
      await device.shell(`monkey -p ${PKG} -c android.intent.category.LAUNCHER 1`);
    }

    let failed;
    const seen = {};
    try {
      await journey(device, attempt, seen);
      break;
    } catch (e) {
      failed = e;
    }
    const message = failed.message ?? String(failed);
    console.error(`${stamp()} attempt ${attempt}: ${await describeFailure(seen.page)}; adb reports: ${adbDevices()}`);
    if (attempt >= ATTEMPTS || (seen.booted && !looksLikeWebViewReplaced(message))) throw failed;
    console.error(`attempt ${attempt} failed (${message.split('\n')[0]}); reconnecting, force-stopping and relaunching once`);
    try { await device.close(); } catch { /* already closed is the case being handled */ }
  }

  console.log('native smoke (android): boot + OTP sign-in + household roster OK');
  await device.close();
  process.exit(0);
}

// Exported so tests/js-logic/smoke.test.js can hold the retry policy and the navigation without a device.
module.exports = { ATTEMPTS, looksLikeWebViewReplaced, navigateToHousehold, waitForOtp };

if (require.main === module)
  main().catch(e => { console.error(`native smoke (android) FAILED: ${e.message}`); process.exit(1); });
