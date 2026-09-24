#Requires -Version 7.0
<#
.SYNOPSIS
  Runs the E2E suite against a local stack wired for testing, without touching .env.

.DESCRIPTION
  Your .env can keep real settings (Brevo mail, Stripe test keys) for everyday development. The E2E journeys need a
  different stack: mail into this repo's Mailpit (they read the codes from it), a raised sign-in rate limit, the fake
  billing provider, and the test staff address. This script starts the API and the web app with exactly those
  overrides as command-line settings (which beat .env), waits until both answer, runs tests/E2E.Tests, and stops both.

  Ports come from the same sources LocalPortsTests reads - the docker-compose defaults and the launch profiles - so
  this file is the same in perezosoft-platform, y-el-vuelto and jigger-jot. Local Dev Alignment L12.

.PARAMETER Filter
  Passed to `dotnet test --filter`, e.g. "FullyQualifiedName~Billing".

.PARAMETER NoBuild
  Skip the build (API, Web and E2E projects are built by default).

.PARAMETER KeepRunning
  Leave the API and web app running after the tests (stop them with Ctrl+C or by closing the window).

.EXAMPLE
  pwsh tools/e2e.ps1
  pwsh tools/e2e.ps1 -Filter "FullyQualifiedName~SignIn"
#>
[CmdletBinding()]
param(
    [string]$Filter,
    [switch]$NoBuild,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# --- this repo's port block ---------------------------------------------------------------------------------------
$compose = Get-Content (Join-Path $root 'docker-compose.yml') -Raw
function Get-ComposeDefault([string]$var) {
    $m = [regex]::Match($compose, '\$\{' + $var + ':-(\d+)\}')
    if (-not $m.Success) { throw "docker-compose.yml has no `${$($var):-<port>} default" }
    $m.Groups[1].Value
}
function Get-HttpsUrl([string]$project) {
    $json = Get-Content (Join-Path $root "src/$project/Properties/launchSettings.json") -Raw
    $m = [regex]::Match($json, 'https://localhost:(\d+)')
    if (-not $m.Success) { throw "src/$project launchSettings has no https://localhost:<port> profile" }
    "https://localhost:$($m.Groups[1].Value)"
}
$smtpPort = Get-ComposeDefault 'MAIL_SMTP_PORT'
$mailUi = "http://localhost:$(Get-ComposeDefault 'MAIL_UI_PORT')"
$api = Get-HttpsUrl 'Api'
$web = Get-HttpsUrl 'Web'
$repo = Split-Path $root -Leaf
Write-Host "E2E for $repo - API $api, web $web, Mailpit $mailUi (SMTP $smtpPort)"

# A dev API already on the port would answer instead - with .env's real mail, so the codes would go out for real.
foreach ($url in @($api, $web)) {
    $uri = [uri]$url
    $probe = [System.Net.Sockets.TcpClient]::new()
    try {
        if ($probe.ConnectAsync($uri.Host, $uri.Port).Wait(500)) {
            throw "Something is already listening on $url. Stop your running API/web app first - the E2E stack needs its own."
        }
    } catch [System.AggregateException] { } finally { $probe.Dispose() }
}

# --- backing services, build, browser -----------------------------------------------------------------------------
docker compose -f (Join-Path $root 'docker-compose.yml') up -d --wait db mail
if ($LASTEXITCODE -ne 0) { throw 'docker compose could not start db + mail (is Docker running?)' }

if (-not $NoBuild) {
    foreach ($project in 'src/Api', 'src/Web', 'tests/E2E.Tests') {
        dotnet build $project -c Debug --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "build failed: $project" }
    }
}
$playwright = Get-ChildItem (Join-Path $root 'tests/E2E.Tests/bin/Debug') -Recurse -Filter 'playwright.ps1' | Select-Object -First 1
if (-not $playwright) { throw 'tests/E2E.Tests is not built (playwright.ps1 missing) - run without -NoBuild' }
pwsh $playwright.FullName install chromium | Out-Null

# --- the stack, wired for testing ---------------------------------------------------------------------------------
$logs = Join-Path ([System.IO.Path]::GetTempPath()) "$repo-e2e"
New-Item -ItemType Directory -Force $logs | Out-Null
$overrides = @(
    '--Email:Smtp:Host=localhost', "--Email:Smtp:Port=$smtpPort", '--Email:Smtp:Username=', '--Email:Smtp:Password=',
    '--Auth:RateLimit:PasswordlessPermitLimit=1000',   # the journeys sign in many users from one IP
    '--Admin:StaffEmails:0=e2e-staff@example.com',     # AnnouncementJourneyTests.StaffEmail
    '--Billing:Enabled=true', '--Billing:Stripe:SecretKey='   # the fake provider's deterministic checkout
)
$started = @()
function Start-App([string]$project, [string[]]$extra) {
    $name = Split-Path $project -Leaf
    $argList = @('run', '--project', $project, '--launch-profile', 'https', '--no-build') + $(if ($extra) { @('--') + $extra } else { @() })
    Start-Process dotnet -ArgumentList $argList -WorkingDirectory $root -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $logs "$name.log") -RedirectStandardError (Join-Path $logs "$name.err.log")
}
function Stop-Apps {
    foreach ($p in $script:started) {
        try { [System.Diagnostics.Process]::GetProcessById($p.Id).Kill($true) } catch { }
    }
}
function Wait-Ready([string]$url, $process, [string]$log) {
    $deadline = (Get-Date).AddMinutes(3)
    while ((Get-Date) -lt $deadline) {
        if ($process.HasExited) { throw "$url exited before it was ready - last lines of ${log}:`n$((Get-Content $log -Tail 20) -join "`n")" }
        try {
            $r = Invoke-WebRequest $url -SkipCertificateCheck -TimeoutSec 5 -SkipHttpErrorCheck
            if ($r.StatusCode -lt 500) { return }
        } catch { }
        Start-Sleep -Milliseconds 800
    }
    throw "$url did not answer within 3 minutes - see $log"
}

$exit = 1
try {
    $apiProc = Start-App 'src/Api' $overrides; $script:started += $apiProc
    $webProc = Start-App 'src/Web' @(); $script:started += $webProc
    Wait-Ready "$api/health/ready" $apiProc (Join-Path $logs 'Api.log')
    Wait-Ready $web $webProc (Join-Path $logs 'Web.log')
    Write-Host 'Stack ready - running tests/E2E.Tests'

    $env:PLAYWRIGHT_BASE_URL = $web
    $env:MAILPIT_BASE_URL = $mailUi
    $env:E2E_API_BASE_URL = $api
    $testArgs = @('test', 'tests/E2E.Tests', '--no-build', '--nologo')
    if ($Filter) { $testArgs += @('--filter', $Filter) }
    dotnet @testArgs
    $exit = $LASTEXITCODE

    if ($KeepRunning) {
        Write-Host "API $api and web $web are still running (logs in $logs). Ctrl+C to stop."
        Wait-Process -Id $apiProc.Id, $webProc.Id
    }
}
finally {
    Stop-Apps
    Write-Host "Stopped the E2E stack. Logs: $logs"
}
exit $exit
