const test = require('node:test');
const assert = require('node:assert/strict');
const { load } = require('./load');

// The guard's whole behaviour: a page restored from the back/forward cache reloads; any other pageshow
// does not. An inverted condition passes a "the strings are in the file" check and fails this one.
function boot() {
  const listeners = {};
  let reloads = 0;
  load('bfcache-guard.js', {
    addEventListener: (type, fn) => { listeners[type] = fn; },
    location: { reload: () => { reloads++; } },
  });
  return { pageshow: persisted => listeners.pageshow({ persisted }), reloads: () => reloads };
}

test('a bfcache restore reloads the page', () => {
  const page = boot();
  page.pageshow(true);
  assert.equal(page.reloads(), 1);
});

test('a normal page show does not reload', () => {
  const page = boot();
  page.pageshow(false);
  assert.equal(page.reloads(), 0);
});
