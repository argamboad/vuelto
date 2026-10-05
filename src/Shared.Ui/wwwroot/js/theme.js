// Theme bootstrap + switcher backend. Loaded in <head> BEFORE the stylesheets in BOTH hosts'
// index.html (src/Web + src/Maui — keep in sync, see docs/NATIVE_PARITY.md) so the saved theme
// applies before first paint (no light flash). Values: "light" | "dark" | "system" (default).
// "system" follows the OS via prefers-color-scheme and tracks live changes.
//
// Order matters (v4 audit NAT-19, R142): window.appTheme is defined and the saved theme applied BEFORE any
// listener is registered, and the listener API is feature-checked. An old WebView whose MediaQueryList has
// no addEventListener used to throw on that line — before appTheme existed — so the saved theme was never
// applied and every theme interop call failed. tests/js-logic/theme.test.js runs this file against stubs.
(function () {
    'use strict';

    var KEY = 'app_theme';
    var media = window.matchMedia('(prefers-color-scheme: dark)');
    var mode = 'system';

    function resolved() {
        return mode === 'system' ? (media.matches ? 'dark' : 'light') : mode;
    }

    // A host with OS-drawn system bars (the Android app) watches every applied theme through
    // SystemBarThemeSync, so its status bar matches the page. Nobody watches on the web.
    var watcher = null;

    // Blazor hands JS a fresh wrapper object per interop call for the same .NET reference; the wrappers
    // share the reference's id, so that is what identifies "the same watcher".
    function same(a, b) {
        return a === b || (!!a && !!b && a._id !== undefined && a._id === b._id);
    }

    function apply() {
        var theme = resolved();
        document.documentElement.setAttribute('data-bs-theme', theme);
        if (watcher) {
            try { watcher.invokeMethodAsync('OnThemeApplied', theme).catch(function () { }); }
            catch (e) { /* the watcher went away with its page */ }
        }
    }

    window.appTheme = {
        // Applies (and remembers in-process) a mode; persistence is the caller's job
        // (IThemePersistence), same split as the culture seam.
        set: function (m) {
            mode = m === 'light' || m === 'dark' ? m : 'system';
            apply();
        },
        current: function () { return mode; },
        // Reports the current theme at once, then every change (a pick, or the OS scheme under "system").
        watch: function (dotNetRef) { watcher = dotNetRef; apply(); },
        // Stops reporting to THAT watcher. A component being disposed passes its own reference, so it can
        // never silence a watcher registered after it; with no argument nothing is cleared.
        unwatch: function (dotNetRef) { if (same(watcher, dotNetRef)) watcher = null; }
    };

    try { window.appTheme.set(localStorage.getItem(KEY)); }
    catch (e) { apply(); /* storage unavailable — render with the OS scheme */ }

    // OS scheme changes only matter while following the system. addListener is the pre-2020 spelling
    // (Android 7's stock WebView); with neither, the theme simply does not track a live OS change.
    function onSchemeChange() { if (mode === 'system') apply(); }
    if (typeof media.addEventListener === 'function') media.addEventListener('change', onSchemeChange);
    else if (typeof media.addListener === 'function') media.addListener(onSchemeChange);
})();
