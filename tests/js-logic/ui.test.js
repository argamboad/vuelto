const test = require('node:test');
const assert = require('node:assert/strict');
const { load } = require('./load');

// ui.js is this app's own bootstrap (the platform has none): a scroll helper and the per-device preferences.
function boot({ storageThrows = false } = {}) {
  const store = new Map();
  const globals = {
    localStorage: {
      getItem: key => { if (storageThrows) throw new Error('storage denied'); return store.has(key) ? store.get(key) : null; },
      setItem: (key, value) => { if (storageThrows) throw new Error('storage denied'); store.set(key, String(value)); },
    },
  };
  return { appUi: load('ui.js', globals).appUi, store };
}

test('scrollIntoView: scrolls smoothly to the top of the element by default', () => {
  const calls = [];
  boot().appUi.scrollIntoView({ scrollIntoView: opts => calls.push(opts) });
  assert.equal(JSON.stringify(calls), JSON.stringify([{ behavior: 'smooth', block: 'start' }])); // built in the sandbox's realm
});

test('scrollIntoView: passes the caller\'s options through', () => {
  const calls = [];
  boot().appUi.scrollIntoView({ scrollIntoView: opts => calls.push(opts) }, { block: 'center' });
  assert.deepEqual(calls, [{ block: 'center' }]);
});

test('scrollIntoView: a missing element, or one that cannot scroll, is ignored', () => {
  const { appUi } = boot();
  assert.doesNotThrow(() => appUi.scrollIntoView(null));
  assert.doesNotThrow(() => appUi.scrollIntoView({}));
});

test('preferences: a value is stored under the pref: prefix and read back', () => {
  const { appUi, store } = boot();
  appUi.setPref('report.view', 'chart');
  assert.deepEqual([...store.entries()], [['pref:report.view', 'chart']]);
  assert.equal(appUi.getPref('report.view'), 'chart');
});

test('preferences: a key never written reads as null', () => {
  assert.equal(boot().appUi.getPref('display.currency'), null);
});

test('preferences: when storage is unavailable, reading gives null and writing does not throw', () => {
  const { appUi } = boot({ storageThrows: true });
  assert.equal(appUi.getPref('report.view'), null);
  assert.doesNotThrow(() => appUi.setPref('report.view', 'chart'));
});
