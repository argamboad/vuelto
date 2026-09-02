// bfcache guard (QA-SEC-03). After sign-out, the browser Back button can restore the last
// authenticated render from the back/forward cache — inert (the refresh call 401s and client-side
// nav is dead), but the stale view is readable on a shared device. A bfcache restore fires
// `pageshow` with `persisted: true`; force a real reload, which re-runs auth and bounces a
// signed-out visitor to /login (a signed-in one just re-lands on the same route). Loaded in BOTH
// hosts' index.html (src/Web + src/Maui — keep in sync, see docs/NATIVE_PARITY.md); browsers only —
// the MAUI WebView never serves bfcache restores, where the listener is a harmless no-op.
window.addEventListener('pageshow', function (e) {
    if (e.persisted) window.location.reload();
});
