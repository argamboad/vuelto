<#
.SYNOPSIS
  Build the Android APK and the Windows app pointed at a host, and hand back the ONE file that installs.
  docs/DEPLOYMENT.md §9.

.DESCRIPTION
  `dotnet publish` leaves two APKs side by side and only one of them installs; Android refuses the other
  in silence. This wrapper publishes, picks the signed one, copies it to <Out>\<app>.apk and prints the
  signature schemes the phone will actually accept, so there is never a choice to get wrong.

.EXAMPLE
  .\tools\publish-native.ps1 -ApiBaseUrl https://myapp-staging.onrender.com          # both platforms
  .\tools\publish-native.ps1 -ApiBaseUrl https://myapp.example.com -Android -Out C:\builds
#>
[CmdletBinding()]
param(
    # A Release build compiles the base URL in — one build, one host (there is no default: shipping a
    # build at the wrong host is worse than a missing argument).
    [Parameter(Mandatory = $true)][string] $ApiBaseUrl,
    # <repo>\out — the same folder DEPLOYMENT §9 publishes to, and gitignored. Do not move it.
    [string] $Out = (Join-Path (Split-Path -Parent $PSScriptRoot) "out"),
    [switch] $Android,
    [switch] $Windows
)
$ErrorActionPreference = "Stop"
if (-not $Android -and -not $Windows) { $Android = $true; $Windows = $true }

$repo = Split-Path -Parent $PSScriptRoot
$proj = Get-ChildItem (Join-Path $repo "src/Maui") -Filter *.csproj | Select-Object -First 1
if (-not $proj) { throw "No MAUI project under src/Maui" }
# The shell project is <App>.Maui; the artifact is the app, so drop the suffix — Vuelto.apk, not Vuelto.Maui.apk.
$app = $proj.BaseName -replace '\.Maui$', ''

New-Item -ItemType Directory -Force $Out | Out-Null

if ($Android) {
    Write-Host "== Android APK -> $ApiBaseUrl" -ForegroundColor Cyan
    $dir = Join-Path $Out "android"
    dotnet publish $proj.FullName -f net10.0-android -c Release -p:ApiBaseUrl=$ApiBaseUrl -p:AndroidPackageFormat=apk -p:AcceptAndroidSDKLicenses=true -o $dir
    if ($LASTEXITCODE -ne 0) { throw "Android publish failed" }

    # Two APKs land here: <id>.apk (unsigned, no MANIFEST — Android drops it without a word) and
    # <id>-Signed.apk. Never hand out the folder; hand out the copy below.
    $signed = Get-ChildItem $dir -Filter *-Signed.apk | Select-Object -First 1
    if (-not $signed) { throw "No *-Signed.apk in $dir — the signing step did not run" }
    $apk = Join-Path $Out ("{0}.apk" -f $app)
    Copy-Item $signed.FullName $apk -Force

    # Prove the scheme the phone requires (v2+; Android 11+ refuses a v1-only APK, silently).
    # apksigner.bat honours JAVA_HOME and dies when it points at a JDK that was uninstalled — a very
    # common state — so drop a stale value and fall back to any JDK we can find.
    if ($env:JAVA_HOME -and -not (Test-Path $env:JAVA_HOME)) { $env:JAVA_HOME = $null }
    if (-not $env:JAVA_HOME -and -not (Get-Command java -ErrorAction SilentlyContinue)) {
        $jdk = Get-ChildItem "$env:ProgramFiles\Eclipse Adoptium", "$env:ProgramFiles\Microsoft\jdk", "$env:ProgramFiles\Java" -Directory -ErrorAction SilentlyContinue |
               Where-Object { Test-Path (Join-Path $_.FullName "bin\java.exe") } | Sort-Object Name | Select-Object -Last 1
        if ($jdk) { $env:JAVA_HOME = $jdk.FullName }
    }
    # The SDK is wherever the toolchain put it: ANDROID_HOME / ANDROID_SDK_ROOT first (what the .NET Android
    # SDK and CI honour), then Visual Studio's install, then Android Studio's (v4 T50 — the old lookup knew
    # only the last one and reported success without verifying anything).
    $sdkRoots = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT,
                  (Join-Path ${env:ProgramFiles(x86)} "Android\android-sdk"),
                  (Join-Path $env:LOCALAPPDATA "Android\Sdk")) | Where-Object { $_ -and (Test-Path $_) }
    $bt = $sdkRoots | ForEach-Object { Get-ChildItem (Join-Path $_ "build-tools") -Directory -ErrorAction SilentlyContinue } |
          Sort-Object { [version]($_.Name -replace '[^\d.].*$','') } | Select-Object -Last 1
    if (-not $bt) { throw "No Android build-tools found (set ANDROID_HOME): the APK's signature scheme cannot be verified, and an unverified APK must not be handed out." }
    if (-not ($env:JAVA_HOME -or (Get-Command java -ErrorAction SilentlyContinue))) { throw "No JDK found for apksigner (set JAVA_HOME): the APK's signature cannot be verified." }
    $verified = & (Join-Path $bt.FullName "apksigner.bat") verify --verbose $apk 2>&1 | Select-String "Verified using v[23] .*true"
    if (-not $verified) { throw "APK signature NOT verified as v2/v3 by apksigner ($apk) — Android 11+ refuses a v1-only APK; do not ship this file." }
    $verified | ForEach-Object { Write-Host $_.Line }

    Write-Host "APK: $apk" -ForegroundColor Green
    Write-Host "Send THIS file. Close the app on the phone first — Android will not replace a running one." -ForegroundColor Yellow
}

if ($Windows) {
    Write-Host "== Windows app -> $ApiBaseUrl" -ForegroundColor Cyan
    $dir = Join-Path $Out "windows"
    dotnet publish $proj.FullName -f net10.0-windows10.0.19041.0 -c Release -p:ApiBaseUrl=$ApiBaseUrl -p:WindowsPackageType=None -o $dir
    if ($LASTEXITCODE -ne 0) { throw "Windows publish failed" }
    Write-Host ("EXE: {0}" -f (Join-Path $dir ("{0}.exe" -f $proj.BaseName))) -ForegroundColor Green
}
