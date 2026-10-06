const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');

// The Android smoke's retry policy, run against fakes (v4 audit T53). The script exports its steps and only
// drives a device when run directly, so loading it here needs neither playwright-core nor an emulator.
const smoke = require(path.join(__dirname, '..', 'native-smoke-android', 'smoke.js'));

function fakeDevice() {
  const shell = [];
  return { shell, device: { shell: async command => { shell.push(command); } } };
}

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

test('boot: a first-attempt failure force-stops, relaunches and tries once more', async () => {
  const { device, shell } = fakeDevice();
  const attempts = [];
  const boot = async (_, attempt) => { attempts.push(attempt); if (attempt === 1) throw new Error('attach race'); return { page: 'p', emailBox: 'e' }; };

  const booted = await smoke.bootWithOneRelaunch(device, boot, 0);

  assert.deepEqual(attempts, [1, 2]);
  assert.equal(shell.length, 2);
  assert.match(shell[0], /^am force-stop /);
  assert.match(shell[1], /^monkey -p .* android\.intent\.category\.LAUNCHER 1$/);
  assert.deepEqual(booted, { page: 'p', emailBox: 'e' });
});

test('boot: a clean first attempt does not relaunch', async () => {
  const { device, shell } = fakeDevice();
  await smoke.bootWithOneRelaunch(device, async () => ({ page: 'p' }), 0);
  assert.equal(shell.length, 0);
});

test('boot: failing BOTH attempts is the real crash and fails the run', async () => {
  const { device } = fakeDevice();
  const attempts = [];
  await assert.rejects(
    smoke.bootWithOneRelaunch(device, async (_, attempt) => { attempts.push(attempt); throw new Error(`crash ${attempt}`); }, 0),
    /crash 2/);
  assert.deepEqual(attempts, [1, 2]); // exactly one retry, never a loop
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
