const test = require('node:test');
const assert = require('node:assert/strict');
const { load, mediaStub, themeGlobals } = require('./load');

function boot(options) {
  const page = load('theme.js', themeGlobals(options));
  return { appTheme: page.appTheme, theme: () => page.attributes['data-bs-theme'], media: page.media };
}

/** A stand-in for a DotNetObjectReference: Blazor gives JS a new wrapper per call, same _id. */
function watcherRef(id) {
  const seen = [];
  return { _id: id, seen, invokeMethodAsync: (method, theme) => { seen.push(`${method}:${theme}`); return Promise.resolve(); } };
}

test('applies the saved theme before first paint', () => {
  assert.equal(boot({ saved: 'dark' }).theme(), 'dark');
  assert.equal(boot({ saved: 'light', media: mediaStub({ dark: true }) }).theme(), 'light');
});

test('an unknown or missing saved value follows the OS', () => {
  assert.equal(boot({ saved: 'purple', media: mediaStub({ dark: true }) }).theme(), 'dark');
  assert.equal(boot({ saved: null }).theme(), 'light');
});

test('storage that throws still renders, with the OS scheme', () => {
  const page = boot({ storageThrows: true, media: mediaStub({ dark: true }) });
  assert.equal(page.theme(), 'dark');
  assert.equal(typeof page.appTheme.set, 'function');
});

test('appTheme exists and the theme is applied BEFORE any listener is registered', () => {
  // The old order registered first: on a WebView where that call throws, appTheme was never defined.
  let atRegistration = null;
  let page;
  const media = mediaStub({ onRegister: () => { atRegistration = { api: typeof page.appTheme, theme: page.attributes['data-bs-theme'] }; } });
  page = themeGlobals({ saved: 'dark', media });
  load('theme.js', page);
  assert.deepEqual(atRegistration, { api: 'object', theme: 'dark' });
});

test('an old WebView without addEventListener still gets appTheme, the saved theme, and live OS changes', () => {
  const media = mediaStub({ api: 'legacy' });
  const page = boot({ saved: 'system', media });
  assert.equal(page.theme(), 'light');
  media.flip(true);
  assert.equal(page.theme(), 'dark');
});

test('a WebView with neither listener API still boots; it just does not track the OS live', () => {
  const page = boot({ saved: 'dark', media: mediaStub({ api: 'none' }) });
  assert.equal(page.theme(), 'dark');
  page.appTheme.set('light');
  assert.equal(page.theme(), 'light');
});

test('OS changes re-apply only while following the system', () => {
  const media = mediaStub();
  const page = boot({ saved: 'light', media });
  media.flip(true);
  assert.equal(page.theme(), 'light');
  page.appTheme.set('system');
  assert.equal(page.theme(), 'dark');
  media.flip(false);
  assert.equal(page.theme(), 'light');
});

test('a watcher hears the current theme at once and every change after', () => {
  const page = boot({ saved: 'dark' });
  const watcher = watcherRef(1);
  page.appTheme.watch(watcher);
  page.appTheme.set('light');
  assert.deepEqual(watcher.seen, ['OnThemeApplied:dark', 'OnThemeApplied:light']);
});

test('unwatch clears only the watcher that asks: a disposed component cannot silence a newer one', () => {
  const page = boot({ saved: 'dark' });
  const first = watcherRef(1);
  const second = watcherRef(2);
  page.appTheme.watch(first);
  page.appTheme.watch(second);       // a second watcher takes the slot

  page.appTheme.unwatch(watcherRef(1)); // the first component is disposed (a fresh wrapper, same id)
  page.appTheme.set('light');
  assert.deepEqual(second.seen, ['OnThemeApplied:dark', 'OnThemeApplied:light']);

  page.appTheme.unwatch();           // no reference: nothing is cleared
  page.appTheme.set('dark');
  assert.equal(second.seen.length, 3);

  page.appTheme.unwatch(watcherRef(2)); // its own reference: now it stops
  page.appTheme.set('light');
  assert.equal(second.seen.length, 3);
});

test('a watcher that throws does not break applying the theme', () => {
  const page = boot({ saved: 'dark' });
  page.appTheme.watch({ _id: 9, invokeMethodAsync: () => { throw new Error('disposed'); } });
  page.appTheme.set('light');
  assert.equal(page.theme(), 'light');
});
