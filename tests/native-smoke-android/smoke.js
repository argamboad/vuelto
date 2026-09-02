// Native smoke — Android (NATIVE-7, ADR-018). Mirrors tests/E2E.Tests/NativeSmokeTests.cs
// (the Windows leg) but in Node: Android WebView's CDP doesn't support the browser-context
// management Playwright's ConnectOverCDPAsync needs, so this leg uses playwright-core's
// _android module instead (adb + WebView attach — the purpose-built path, Node-only).
//
// Prereqs (the CI job or a local rehearsal provides them): an emulator/device with the DEBUG
// app installed and launched (EmbedAssembliesIntoApk=true — a fast-deployment APK won't start
// from a plain `adb install`), `adb reverse tcp:5238 tcp:5238`, the API on
// http://localhost:5238, Mailpit on MAILPIT_BASE_URL (default http://localhost:8025).
const { _android } = require('playwright-core');

const PKG = process.env.NATIVE_SMOKE_PKG || 'com.perezosoft.platform';
const MAILPIT = process.env.MAILPIT_BASE_URL || 'http://localhost:8025';

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

(async () => {
  const devices = await _android.devices();
  if (devices.length === 0) throw new Error('no adb device/emulator attached');
  const device = devices[0];
  console.log(`device: ${device.serial()}`);

  // ONE relaunch retry, boot phase only: MAUI's BlazorWebView has a startup race where an early
  // Android Activity recreate disposes the service scope while the attach IPC is in flight —
  // "Cannot access a disposed object: 'IServiceProvider'" at WebViewManager.AttachToPageAsync —
  // and the login page then never renders (run 32769356890; the same APK passed twice that
  // morning). A single force-stop + relaunch distinguishes that transient race from a real
  // startup crash: the G7 class this canary exists for fails BOTH attempts.
  let boot;
  try {
    boot = await bootToLogin(device, 1);
  } catch (e) {
    console.error(`boot attempt 1 failed (${e.message}); force-stopping and relaunching once (MAUI attach race)`);
    await device.shell(`am force-stop ${PKG}`);
    await new Promise(r => setTimeout(r, 2000));
    await device.shell(`monkey -p ${PKG} -c android.intent.category.LAUNCHER 1`);
    boot = await bootToLogin(device, 2);
  }
  const { page, emailBox } = boot;

  // OTP sign-in end-to-end through the real API + Mailpit (the native body-token transport).
  const email = `native-smoke-${Date.now()}@example.com`;
  await mailpit('/api/v1/messages', { method: 'DELETE' });
  await emailBox.fill(email);
  await page.getByTestId('login-send-otp').click();
  const code = await waitForOtp(email, 60_000);
  await page.getByTestId('login-otp-code').fill(code);
  await page.getByTestId('login-verify-otp').click();
  // Attached, not visible: the responsive header collapses sign-out behind the hamburger on
  // a phone-sized window (same reasoning as the Windows leg).
  await page.getByTestId('sign-out').first().waitFor({ state: 'attached', timeout: 60_000 });

  // One authorized page: Household loads its data — proves the native Bearer path.
  await page.goto('https://0.0.0.1/household');
  await page.getByTestId('household-rename-input').waitFor({ state: 'visible', timeout: 60_000 });
  const members = await page.getByTestId('member-row').count();
  if (members !== 1) throw new Error(`expected 1 roster row for a fresh owner, saw ${members}`);

  console.log('native smoke (android): boot + OTP sign-in + household roster OK');
  await device.close();
  process.exit(0);
})().catch(e => { console.error(`native smoke (android) FAILED: ${e.message}`); process.exit(1); });
