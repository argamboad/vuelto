#Requires -Version 7.0
<#
.SYNOPSIS
  Boots an Android emulator (AVD) on a fixed console port, or reuses it if it is already up there, and waits until
  Android has finished booting. If another AVD holds the port, or this AVD runs on another port, it closes that
  emulator first.

.DESCRIPTION
  The "API + Android phone/tablet (emulator)" debug profiles need the emulator to have a known adb serial before the
  app is deployed: the VS Code MAUI extension takes the target as an adb serial, and an emulator's serial is
  emulator-<console port>. Starting it here on a fixed port makes that serial fixed too:
      phone  -> AVD "phone"  on 5554 -> emulator-5554
      tablet -> AVD "tablet" on 5556 -> emulator-5556
  Visual Studio boots the emulator itself (the toolbar's Debug Target) on the next free port, so it does not need
  this script, but that is how the phone can end up on 5556 - which this script then undoes.
  The SDK is the one Visual Studio uses (ANDROID_HOME if set, else Android Studio's %LOCALAPPDATA%\Android\Sdk, else
  the VS-installed one), so this adb and VS's adb are the same binary and don't kill each other's server.
  Called by the emulator tasks in .vscode/tasks.json; the profiles come from tools/dev-profiles.ps1.
  The same file lives in perezosoft-platform, y-el-vuelto and jigger-jot.

.PARAMETER Avd
  The AVD name (`emulator -list-avds`).

.PARAMETER Port
  The even console port to boot it on, 5554-5584. The adb serial is emulator-<Port>.

.EXAMPLE
  pwsh tools/android-emulator.ps1 -Avd phone -Port 5554
  pwsh tools/android-emulator.ps1 -Avd tablet -Port 5556
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Avd,
    [Parameter(Mandatory)][ValidateScript({ $_ % 2 -eq 0 -and $_ -ge 5554 -and $_ -le 5584 })][int]$Port,
    [int]$TimeoutSeconds = 240
)

$ErrorActionPreference = 'Stop'

$sdk = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, (Join-Path $env:LOCALAPPDATA 'Android\Sdk'),
        (Join-Path ${env:ProgramFiles(x86)} 'Android\android-sdk')) |
    Where-Object { $_ -and (Test-Path (Join-Path $_ 'emulator\emulator.exe')) } | Select-Object -First 1
if (-not $sdk) { throw 'No Android SDK with an emulator found (ANDROID_HOME, %LOCALAPPDATA%\Android\Sdk, Program Files (x86)).' }
$emulator = Join-Path $sdk 'emulator\emulator.exe'
$adb = Join-Path $sdk 'platform-tools\adb.exe'
$serial = "emulator-$Port"

if ((& $emulator -list-avds) -notcontains $Avd) {
    throw "No AVD named '$Avd'. Create it in Android Studio's Device Manager (or VS's Android Device Manager) first."
}

& $adb start-server | Out-Null

# serial -> AVD name for every running emulator.
function Get-RunningEmulators {
    $map = @{}
    foreach ($line in (& $adb devices)) {
        if ($line -match '^(emulator-\d+)\s+device') {
            $map[$Matches[1]] = ((& $adb -s $Matches[1] emu avd name 2>$null) | Select-Object -First 1)?.Trim()
        }
    }
    $map
}

# Anything else (Visual Studio, Android Studio) boots emulators on the next free port, so the phone can end up on
# the tablet's port or the other way round. Close whatever holds our port with another AVD, and our AVD if it is
# running on another port (an AVD can't run twice), then boot it where the profile expects it.
$running = Get-RunningEmulators
$wrong = @($running.Keys | Where-Object { ($_ -eq $serial) -ne ($running[$_] -eq $Avd) })
foreach ($s in $wrong) {
    Write-Host "Closing $s (AVD '$($running[$s])') so $Avd can run as $serial..."
    & $adb -s $s emu kill | Out-Null
}
if ($wrong) {
    $closeBy = (Get-Date).AddSeconds(60)
    while (@((Get-RunningEmulators).Keys | Where-Object { $_ -in $wrong }).Count -gt 0) {
        if ((Get-Date) -gt $closeBy) { throw "Could not close $($wrong -join ', ') within 60 s." }
        Start-Sleep -Seconds 1
    }
    Start-Sleep -Seconds 2   # let the emulator release the AVD's lock files
}

if ($running[$serial] -eq $Avd) {
    Write-Host "$Avd is already running as $serial."
} else {
    Write-Host "Booting $Avd as $serial..."
    Start-Process -FilePath $emulator -ArgumentList '-avd', $Avd, '-port', $Port -WindowStyle Hidden
}

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((& $adb -s $serial shell getprop sys.boot_completed 2>$null) -ne '1') {
    if ((Get-Date) -gt $deadline) { throw "$Avd did not finish booting within $TimeoutSeconds s." }
    Start-Sleep -Seconds 2
}
Write-Host "$Avd is ready as $serial."
