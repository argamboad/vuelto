#Requires -Version 7.0
<#
.SYNOPSIS
  Boots an Android emulator (AVD) on a fixed console port, or reuses it if it is already up, and waits until Android
  has finished booting.

.DESCRIPTION
  The "API + Android phone/tablet (emulator)" debug profiles need the emulator to have a known adb serial before the
  app is deployed: the VS Code MAUI extension takes the target as an adb serial, and an emulator's serial is
  emulator-<console port>. Starting it here on a fixed port makes that serial fixed too:
      phone  -> AVD "phone"  on 5554 -> emulator-5554
      tablet -> AVD "tablet" on 5556 -> emulator-5556
  Visual Studio boots the emulator itself from the profile's DebugTarget, so it does not need this script.
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
$state = (& $adb -s $serial get-state 2>$null)
if ($state -eq 'device') {
    $running = ((& $adb -s $serial emu avd name 2>$null) | Select-Object -First 1)?.Trim()
    if ($running -and $running -ne $Avd) { throw "$serial is already running AVD '$running', not '$Avd'." }
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
