// Small DOM helpers the shared UI calls through JS interop. Loaded by BOTH hosts' index.html
// (src/Web + src/Maui — keep in sync, see docs/NATIVE_PARITY.md).
(function () {
    'use strict';
    window.appUi = {
        // Bring an element into view (e.g. a form card rendered above the row that opened it).
        scrollIntoView: function (el, opts) {
            if (el && typeof el.scrollIntoView === 'function') el.scrollIntoView(opts || { behavior: 'smooth', block: 'start' });
        },
        // Per-device UI preferences (e.g. the report's table/chart view) — never account data.
        getPref: function (key) { try { return localStorage.getItem('pref:' + key); } catch (e) { return null; } },
        setPref: function (key, value) { try { localStorage.setItem('pref:' + key, value); } catch (e) { /* storage unavailable */ } }
    };
})();
