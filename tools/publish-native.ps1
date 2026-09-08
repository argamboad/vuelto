<#
.SYNOPSIS
  Build the Android APK and the Windows app pointed at a host (staging by default). docs/DEPLOYMENT.md §9.

.EXAMPLE
  .\tools\publish-native.ps1                                   # both, against staging, into ~\vuelto-builds
  .\tools\publish-native.ps1 -ApiBaseUrl https://vuelto.example.com -Out C:\builds -Android
#>
[CmdletBinding()]
param(
    [string] $ApiBaseUrl = "https://vuelto-staging.onrender.com",
    [string] $Out = (Join-Path $HOME "vuelto-builds"),
    [switch] $Android,
    [switch] $Windows
)
$ErrorActionPreference = "Stop"
if (-not $Android -and -not $Windows) { $Android = $true; $Windows = $true }
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo "src/Maui/Vuelto.Maui.csproj"
New-Item -ItemType Directory -Force $Out | Out-Null

if ($Android) {
    Write-Host "== Android APK -> $ApiBaseUrl" -ForegroundColor Cyan
    $dir = Join-Path $Out "android"
    dotnet publish $proj -f net10.0-android -c Release -p:ApiBaseUrl=$ApiBaseUrl -p:AndroidPackageFormat=apk -p:AcceptAndroidSDKLicenses=true -o $dir
    if ($LASTEXITCODE -ne 0) { throw "Android publish failed" }
    $apk = Join-Path $dir "com.perezosoft.vuelto-Signed.apk"
    Copy-Item $apk (Join-Path $Out "vuelto.apk") -Force
    # Prove the signature scheme the phone will accept (v2+), when the SDK build-tools + a JDK are around.
    # apksigner.bat honours JAVA_HOME and dies when it points at a removed JDK — fall back to the java on PATH.
    if ($env:JAVA_HOME -and -not (Test-Path $env:JAVA_HOME)) { Remove-Item Env:JAVA_HOME }
    $bt = Get-ChildItem (Join-Path $env:LOCALAPPDATA "Android/Sdk/build-tools") -Directory -ErrorAction SilentlyContinue | Sort-Object { [version]($_.Name -replace '[^\d.].*$','') } | Select-Object -Last 1
    if ($bt -and (Get-Command java -ErrorAction SilentlyContinue)) {
        & (Join-Path $bt.FullName "apksigner.bat") verify --verbose $apk 2>&1 | Select-String "Verified using v[123] "
    } else { Write-Host "(apksigner/java not found — skipping the signature check)" -ForegroundColor Yellow }
    Write-Host "APK: $(Join-Path $Out 'vuelto.apk')" -ForegroundColor Green
}

if ($Windows) {
    Write-Host "== Windows app -> $ApiBaseUrl" -ForegroundColor Cyan
    $dir = Join-Path $Out "windows"
    dotnet publish $proj -f net10.0-windows10.0.19041.0 -c Release -p:ApiBaseUrl=$ApiBaseUrl -p:WindowsPackageType=None -o $dir
    if ($LASTEXITCODE -ne 0) { throw "Windows publish failed" }
    Write-Host "EXE: $(Join-Path $dir 'Vuelto.Maui.exe')" -ForegroundColor Green
}
