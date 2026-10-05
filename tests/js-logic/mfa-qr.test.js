const test = require('node:test');
const assert = require('node:assert/strict');
const { load } = require('./load');

function boot({ withLibrary = true, element = { innerHTML: '' }, throws = false } = {}) {
  const calls = [];
  const globals = {
    document: { getElementById: id => (id === 'qr' ? element : null) },
    console: { error: () => { } },
  };
  if (withLibrary) {
    globals.qrcode = (type, ecc) => ({
      addData: data => { if (throws) throw new Error('too long'); calls.push(data); },
      make: () => { },
      createSvgTag: () => '<svg data-qr></svg>',
    });
  }
  const sandbox = load('mfa-qr.js', globals);
  return { mfaQr: sandbox.mfaQr, element, calls };
}

test('renders the provisioning URI into the element', () => {
  const page = boot();
  assert.equal(page.mfaQr.render('qr', 'otpauth://totp/x?secret=ABC'), true);
  assert.equal(page.element.innerHTML, '<svg data-qr></svg>');
  assert.deepEqual(page.calls, ['otpauth://totp/x?secret=ABC']);
});

test('returns false, and draws nothing, when the vendored library is missing', () => {
  const page = boot({ withLibrary: false });
  assert.equal(page.mfaQr.render('qr', 'otpauth://totp/x'), false);
  assert.equal(page.element.innerHTML, '');
});

test('returns false for a missing element, an empty URI, or data the library refuses', () => {
  assert.equal(boot().mfaQr.render('nope', 'otpauth://totp/x'), false);
  assert.equal(boot().mfaQr.render('qr', ''), false);
  assert.equal(boot({ throws: true }).mfaQr.render('qr', 'otpauth://totp/x'), false);
});

test('clear empties a rendered QR and ignores a missing element', () => {
  const page = boot({ element: { innerHTML: '<svg></svg>' } });
  page.mfaQr.clear('qr');
  assert.equal(page.element.innerHTML, '');
  page.mfaQr.clear('nope');
});
