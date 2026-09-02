# Manual testing — Android (MAUI)

How to run and sign in to the Android app against your local API. Covers **email OTP**,
**Google/Microsoft OAuth**, and **"remember me"** (session survives an app restart).

The trick that makes everything work — including OAuth — is **`adb reverse`**: it maps the
device's `localhost:5238` to your host machine, so the app talks to the API at
`http://localhost:5238`. Using `localhost` (not the emulator's `10.0.2.2` alias) is what
lets OAuth succeed, because Google/Microsoft accept `localhost` as a redirect host but
reject raw IPs. The app's Android base URL is already set to `http://localhost:5238`.

## 1. Prerequisites (host)

Start the backing services and the API:

```bash
docker compose up -d                                   # Postgres (5434) + Mailpit (1026/8026) — the vuelto stack
dotnet run --project src/Api --launch-profile https    # serves https:7160 AND http:5238
```

The `https` profile binds **both** `https://localhost:7160` (web/desktop) and
`http://localhost:5238` (mobile) in one run. `UseHttpsRedirection` is disabled in
Development, so the cleartext `:5238` leg is not redirected.

An Android emulator (AVD) running, or a physical device with USB debugging enabled.

## 2. Bridge the device to the host

**Usually automatic:** the csproj's `AndroidReverseDevApiPort` target re-runs the bridge on
**every Debug build/deploy** — CLI installs *and* VS F5 (the project disables VS's fast
up-to-date check in Debug so the hook can't be skipped). So after an emulator reboot, just
build/F5 again and the bridge is back.

To set it by hand (it does not persist across emulator/device restarts):

```bash
adb reverse tcp:5238 tcp:5238
```

Verify: `adb reverse --list` should show `tcp:5238 tcp:5238`. From then on, anything on the
device hitting `localhost:5238` (the app *and* the in-app browser tab) reaches your host API.

## 3. Run the app

From Visual Studio: select the Android target + your emulator, F5. Or CLI (emulator already
running):

```bash
dotnet build src/Maui/Vuelto.Maui.csproj -t:Run -f net10.0-android
```

## 4. Test email OTP (no extra setup)

1. On the login screen, enter an email and tap **Email me a 6-digit code**.
2. Open Mailpit on the host: <http://localhost:8026>, copy the code.
3. Enter it in the app → you're signed in.

## 5. Test Google / Microsoft OAuth

OAuth needs the provider to accept the redirect URI the API will use,
`http://localhost:5238/signin-{provider}`. **Providers allow `http://localhost` (any port)**,
so register these once in each console:

| Provider | Redirect URI to register |
|---|---|
| Google (OAuth client → Authorized redirect URIs) | `http://localhost:5238/signin-google` |
| Microsoft (App registration → Authentication → Web → Redirect URIs) | `http://localhost:5238/signin-microsoft` |

> The Google one is likely already registered — it's the same URI the desktop/web flow uses.
> Microsoft typically needs `http://localhost:5238/signin-microsoft` added.

Then in the app tap **Continue with Google/Microsoft** → a browser tab opens → sign in →
the tab shows "you can close this" → the app completes sign-in. (Account **linking** from
Settings works the same way.)

## 6. Test "remember me"

Fully close the app and reopen it. You should land signed in (the refresh token is held in
the Android Keystore and silently exchanged on startup).

## Troubleshooting

- **Everything fails / spinner forever** → `adb reverse` not set (re-run step 2), or the API
  isn't running. Confirm from the host: `curl http://localhost:5238/api/auth/refresh -X POST`
  returns `401` (not a connection error).
- **OAuth: "redirect_uri_mismatch" / "reply URL does not match"** → the
  `http://localhost:5238/signin-{provider}` URI isn't registered for that provider (step 5).
- **OAuth tab opens but never returns to the app** → the `vuelto://auth` intent filter
  didn't match; confirm `MauiProgram.CallbackScheme`, the API's `Auth:Native:CallbackScheme`,
  and `WebAuthenticatorCallbackActivity`'s scheme are all `vuelto`.
- **Cleartext blocked** → the app talks HTTP to `localhost`, permitted by
  `Platforms/Android/Resources/xml/network_security_config.xml`. If you change the host,
  add it there.
- **VS breaks on `TypeError: Failed to execute 'query' on 'Permissions': Illegal invocation`
  during Google sign-in** (any F5 run — web or Windows shell) → not an app bug. It's Google's
  own obfuscated anti-abuse script (an eval'd `VM…` blob) probing `navigator.permissions.query`;
  the throw is expected and handled by Google's code, but the debugger VS attaches to the
  browser/WebView (on web via the Blazor WASM debug proxy — the `inspectUri` in
  `src/Web/Properties/launchSettings.json`; the "Enable JavaScript debugging for ASP.NET"
  option does **not** control this) can't see the handler and reports it "unhandled".
  Continue (F5) and sign-in proceeds. `inspectUri` was removed from the Web launch profiles
  (2026-07-08) precisely because of this, so on web VS no longer attaches to the browser at
  all — the trade-off is no C# breakpoints inside the WASM client (browser F12 still works).
  If you re-add `inspectUri` to debug WASM C#, silence the break via Debug → Windows →
  Exception Settings (Ctrl+Alt+E) → uncheck the **JavaScript Exceptions** category.
- **Physical device** → `adb reverse` works over USB too; no other change needed.
- **Pointing the app somewhere else** → set `VUELTO_API_BASE_URL` before launching (any
  platform): overrides the compiled per-platform API base — e.g. a LAN address for a device that
  can't use `adb reverse`, or plain HTTP for the CI native smoke (`native-smoke-windows`).
