// Runs one of the RCL's wwwroot/js bootstrap files inside a stub browser (v4 audit T53, R142).
// These files are plain scripts with no module system, so each test builds the globals the file touches,
// evaluates the real source in that sandbox, and asserts on what it did. No DOM library, no install step:
// `node --test tests/js-logic` needs Node and nothing else.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const JS_DIR = path.join(__dirname, '..', '..', 'src', 'Shared.Ui', 'wwwroot', 'js');

/** Evaluates wwwroot/js/<file> with `globals` AS its global object (and as `window`), and returns it. */
function load(file, globals) {
  globals.window = globals;
  vm.createContext(globals);
  vm.runInContext(fs.readFileSync(path.join(JS_DIR, file), 'utf8'), globals, { filename: file });
  return globals;
}

/** A matchMedia stub. `api`: 'modern' (addEventListener), 'legacy' (addListener only) or 'none'. */
function mediaStub({ dark = false, api = 'modern', onRegister } = {}) {
  const listeners = [];
  const media = { matches: dark };
  const register = fn => { if (onRegister) onRegister(); listeners.push(fn); };
  if (api === 'modern') media.addEventListener = (type, fn) => { if (type === 'change') register(fn); };
  if (api === 'legacy') media.addListener = register;
  media.flip = value => { media.matches = value; listeners.forEach(fn => fn({ matches: value })); };
  media.listenerCount = () => listeners.length;
  return media;
}

/** The page theme.js runs in: a root element, localStorage, matchMedia. `attributes` records what it set. */
function themeGlobals({ saved = null, storageThrows = false, media = mediaStub() } = {}) {
  const attributes = {};
  return {
    matchMedia: () => media,
    document: { documentElement: { setAttribute: (k, v) => { attributes[k] = v; } } },
    localStorage: { getItem: () => { if (storageThrows) throw new Error('storage denied'); return saved; } },
    attributes,
    media,
  };
}

module.exports = { load, mediaStub, themeGlobals, JS_DIR };
