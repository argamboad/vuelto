// Theme bootstrap + switcher backend. Loaded in <head> BEFORE the stylesheets in BOTH hosts'
// index.html (src/Web + src/Maui — keep in sync, see docs/NATIVE_PARITY.md) so the saved theme
// applies before first paint (no light flash). Values: "light" | "dark" | "system" (default).
// "system" follows the OS via prefers-color-scheme and tracks live changes.
(function () {
    'use strict';

    var KEY = 'app_theme';
    var media = window.matchMedia('(prefers-color-scheme: dark)');
    var mode = 'system';

    function resolved() {
        return mode === 'system' ? (media.matches ? 'dark' : 'light') : mode;
    }

    function apply() {
        document.documentElement.setAttribute('data-bs-theme', resolved());
    }

    // OS scheme changes only matter while following the system.
    media.addEventListener('change', function () { if (mode === 'system') apply(); });

    window.appTheme = {
        // Applies (and remembers in-process) a mode; persistence is the caller's job
        // (IThemePersistence), same split as the culture seam.
        set: function (m) {
            mode = m === 'light' || m === 'dark' ? m : 'system';
            apply();
        },
        current: function () { return mode; }
    };

    try { window.appTheme.set(localStorage.getItem(KEY)); }
    catch (e) { apply(); /* storage unavailable — render with the OS scheme */ }
})();
