const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');

// The Android smoke's retry policy, run against fakes (v4 audit T53). The script exports its steps and only
// drives a device when run directly, so loading it here needs neither playwright-core nor an emulator.
const smoke = require(path.join(__dirname, '..', 'native-smoke-android', 'smoke.js'));


/** A page whose in-app navigation works, or fails at the step named. */
function fakePage({ menuVisible = true, failAt = null } = {}) {
  const log = [];
  const step = name => async () => { log.push(name); if (failAt === name) throw new Error(`${name} timed out`); };
  return {
    log,
    // Here Household sits inside the user menu: menu first, then the link.
    getByTestId: id => id === 'user-menu'
      ? { isVisible: async () => menuVisible, click: step('open-user-menu') }
      : id === 'nav-household'
        ? { click: step('click-link') }
        : { waitFor: step(log.includes('goto') ? 'household-after-goto' : 'household') },
    locator: () => ({ click: step('open-hamburger') }),
    goto: async url => { log.push('goto'); },
  };
}

function captureLog(fn) {
  const lines = [];
  const original = console.log;
  console.log = line => lines.push(String(line));
  return fn().finally(() => { console.log = original; }).then(result => ({ result, lines }));
}

// The WHOLE journey is retried once, and only for the shapes a replaced WebView takes. These hold that policy.
test('retry: the journey is tried twice, never more', () => {
  assert.equal(smoke.ATTEMPTS, 2);
});

test('retry: the shapes a replaced WebView takes are retried', () => {
  for (const message of [
    'page.fill: Target page, context or browser has been closed',
    'Protocol error: Target closed',
    'page.evaluate: Execution context was destroyed, most likely because of a navigation',
    'androidDevice.shell: Device is closed',
    "locator.fill: Timeout 30000ms exceeded. Call log: - waiting for getByTestId('login-email')",
  ]) assert.equal(smoke.looksLikeWebViewReplaced(message), true, message);
});

test('retry: an app fault after boot is not mistaken for a replaced WebView', () => {
  for (const message of [
    'expected 1 roster row for a fresh owner, saw 0',
    'no OTP email arrived within 60000 ms',
    "locator.click: Timeout 60000ms exceeded. Call log: - waiting for getByTestId('login-verify-otp')",
  ]) assert.equal(smoke.looksLikeWebViewReplaced(message), false, message);
});

test('household: a visible user menu is opened and the link clicked, with no warning', async () => {
  const page = fakePage();
  const { result, lines } = await captureLog(() => smoke.navigateToHousehold(page, 1));
  assert.equal(result, 'in-app');
  assert.deepEqual(page.log, ['open-user-menu', 'click-link', 'household']);
  assert.equal(lines.filter(l => l.startsWith('::warning')).length, 0);
});

test('household: a collapsed header opens the hamburger first', async () => {
  const page = fakePage({ menuVisible: false });
  assert.equal(await smoke.navigateToHousehold(page, 1), 'in-app');
  assert.deepEqual(page.log, ['open-hamburger', 'open-user-menu', 'click-link', 'household']);
});

test('household: the full-load fallback runs once and raises a workflow warning', async () => {
  const page = fakePage({ failAt: 'click-link' });
  const { result, lines } = await captureLog(() => smoke.navigateToHousehold(page, 1));
  assert.equal(result, 'fallback');
  assert.equal(page.log.filter(s => s === 'goto').length, 1);
  const warnings = lines.filter(l => l.startsWith('::warning'));
  assert.equal(warnings.length, 1);
  assert.match(warnings[0], /in-app navigation to Household failed \(click-link timed out\)/);
});

test('household: when the fallback fails too, the run fails', async () => {
  const page = fakePage({ failAt: 'click-link' });
  page.goto = async () => { throw new Error('reload race'); };
  await assert.rejects(captureLog(() => smoke.navigateToHousehold(page, 1)), /reload race/);
});
