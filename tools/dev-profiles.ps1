#Requires -Version 7.0
<#
.SYNOPSIS
  Writes this clone's start profiles for Visual Studio and VS Code - the same six names in both - into gitignored
  files. Run it once per clone (and again after adding a device or an AVD).

.DESCRIPTION
  The profiles:
      API + Web                          API + Android phone (device)    API + Android phone (emulator)
      API + Windows desktop              API + Android tablet (device)   API + Android tablet (emulator)
  Visual Studio reads them from <Sln>.slnLaunch.user (the startup dropdown); VS Code from the "launch" block of
  .vscode/settings.json (Run and Debug). Both files are gitignored, because the Android ones name the developer's own
  devices, which a clone has no use for. Everything else is read from the repo - the solution name from the .slnx, the
  ports from the launchSettings.json files, the target frameworks from the csprojs - so this works unchanged after a
  rebrand. The build and emulator tasks the VS Code profiles run are in .vscode/tasks.json.

  Devices come from a file outside every repo (default ~/dev-tools/devices.json):
      { "phone":  { "vs": "Samsung SM-S918B (Android 16.0 - API 36)", "adb": "adb-R5CW...._adb-tls-connect._tcp" },
        "tablet": { "vs": "...", "adb": "..." } }
  "vs" is the name Visual Studio lists the device under; "adb" is its serial in `adb devices`, which is what the VS
  Code MAUI extension targets. -Discover writes the file from the devices adb has connected right now. Over Wi-Fi
  debugging the serial is the mDNS name, which stays the same across reconnects as long as the device is on the same
  network as the PC; an IP:port serial (USB-less connect through a VPN) changes every time and won't keep working.
  Emulators are the AVDs named `phone` and `tablet`: VS boots them itself, VS Code's profiles boot them on fixed ports
  first (tools/android-emulator.ps1), which gives them the fixed serials emulator-5554 / emulator-5556.
  A missing device or AVD just leaves its profile out, with a note. Profiles of your own in either file, under other
  names, are kept. The same file lives in perezosoft-platform, y-el-vuelto and jigger-jot.

.PARAMETER Discover
  Record the phone and tablet adb has connected now (by ro.build.characteristics) in the devices file, then write
  the profiles. A role that isn't connected keeps what the file already had.

.PARAMETER DevicesFile
  The devices file. Default ~/dev-tools/devices.json.

.EXAMPLE
  pwsh tools/dev-profiles.ps1
  pwsh tools/dev-profiles.ps1 -Discover
#>
[CmdletBinding()]
param(
    [switch]$Discover,
    [string]$DevicesFile = (Join-Path $HOME 'dev-tools/devices.json')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

# --- the Android SDK (the same resolution as tools/android-emulator.ps1, so both use VS's adb) ---
$sdk = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, (Join-Path $env:LOCALAPPDATA 'Android/Sdk'),
        (Join-Path ${env:ProgramFiles(x86)} 'Android/android-sdk')) |
    Where-Object { $_ -and (Test-Path (Join-Path $_ 'platform-tools/adb.exe')) } | Select-Object -First 1
$adb = $sdk ? (Join-Path $sdk 'platform-tools/adb.exe') : $null

# VS lists an Android target as "<name> (Android <release> - API <level>)", the release always with a minor part.
function VsName([string]$name, [string]$release, [string]$api) {
    if ($release -notmatch '\.') { $release = "$release.0" }
    "$name (Android $release - API $api)"
}

# --- -Discover: record the connected phone/tablet ---
if ($Discover) {
    if (-not $adb) { throw 'No Android SDK found (ANDROID_HOME, %LOCALAPPDATA%\Android\Sdk, Program Files (x86)).' }
    # Merge: a device that isn't connected right now keeps its entry.
    $devices = (Test-Path $DevicesFile) ? (Get-Content $DevicesFile -Raw | ConvertFrom-Json -AsHashtable) : [ordered]@{}
    $found = @{}
    $serials = & $adb devices | Select-Object -Skip 1 | Where-Object { $_ -match '^(\S+)\s+device$' } | ForEach-Object { ($_ -split '\s+')[0] }
    foreach ($serial in $serials | Where-Object { $_ -notlike 'emulator-*' }) {
        $prop = { param($p) (& $adb -s $serial shell getprop $p).Trim() }
        $role = (& $prop 'ro.build.characteristics') -match 'tablet' ? 'tablet' : 'phone'
        $maker = & $prop 'ro.product.manufacturer'
        $maker = $maker.Substring(0, 1).ToUpper() + $maker.Substring(1)
        $vs = VsName "$maker $(& $prop 'ro.product.model')" (& $prop 'ro.build.version.release') (& $prop 'ro.build.version.sdk')
        if ($found[$role]) { Write-Warning "More than one $role connected; keeping $($devices[$role].adb), skipping $serial."; continue }
        $found[$role] = $true
        if ($serial -match '^\d+\.\d+\.\d+\.\d+:\d+$') { Write-Warning "$vs is connected as $serial, which changes on every reconnect. Put it on the PC's Wi-Fi so adb finds it by its mDNS name." }
        $devices[$role] = [ordered]@{ vs = $vs; adb = $serial }
        Write-Host "Found $role`: $vs ($serial)"
    }
    if ($found.Count -eq 0) { throw 'adb has no phone or tablet connected. Pair it (Wireless debugging -> Pair device with pairing code, then adb pair <ip>:<port>) and try again.' }
    New-Item -ItemType Directory -Force (Split-Path $DevicesFile) | Out-Null
    $devices | ConvertTo-Json | Set-Content $DevicesFile
    Write-Host "Saved $DevicesFile"
}

# --- what the repo says ---
$sln = Get-ChildItem $repo -Filter *.slnx | Select-Object -First 1
if (-not $sln) { throw "No .slnx in $repo." }
function Project([string]$folder) {
    $p = Get-ChildItem (Join-Path $repo "src/$folder") -Filter *.csproj | Select-Object -First 1
    if (-not $p) { throw "No project in src/$folder." }
    $p
}
$api, $web, $maui = (Project Api), (Project Web), (Project Maui)
function HttpsProfileUrls([string]$folder) {
    $settings = Get-Content (Join-Path $repo "src/$folder/Properties/launchSettings.json") -Raw | ConvertFrom-Json
    $settings.profiles.https.applicationUrl -split ';'
}
$apiUrls = HttpsProfileUrls Api
$webUrl = HttpsProfileUrls Web | Where-Object { $_ -like 'https://*' } | Select-Object -First 1
$apiTfm = ([xml](Get-Content $api.FullName -Raw)).Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1
$mauiText = Get-Content $maui.FullName -Raw
$androidTfm = [regex]::Match($mauiText, 'net[\d.]+-android').Value
$windowsTfm = [regex]::Match($mauiText, 'net[\d.]+-windows[\d.]+').Value
$rid = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64' ? 'win-arm64' : 'win-x64'

# --- targets: (profile, VS DebugTarget, VS Code adb serial, emulator AVD or $null) ---
$targets = [System.Collections.Generic.List[object]]::new()
$known = (Test-Path $DevicesFile) ? (Get-Content $DevicesFile -Raw | ConvertFrom-Json) : $null
$avdHome = $env:ANDROID_AVD_HOME ? $env:ANDROID_AVD_HOME : (Join-Path $HOME '.android/avd')
$emulatorPort = @{ phone = 5554; tablet = 5556 }
foreach ($role in 'phone', 'tablet') {
    $device = $known.$role
    if ($device.vs -and $device.adb) { $targets.Add(@("Android $role (device)", $device.vs, $device.adb, $null)) }
    else { Write-Host "No $role in $DevicesFile - skipping 'API + Android $role (device)' (connect it and run with -Discover)." }

    $config = Join-Path $avdHome "$role.avd/config.ini"
    if ($sdk -and (Test-Path $config)) {
        $ini = @{}; Get-Content $config | Where-Object { $_ -match '^([^=]+)=(.*)$' } | ForEach-Object { $ini[$Matches[1].Trim()] = $Matches[2].Trim() }
        $build = @{}; Get-Content (Join-Path $sdk ($ini['image.sysdir.1'] + 'build.prop')) |
            Where-Object { $_ -match '^(ro\.build\.version\.(release|sdk))=(.*)$' } | ForEach-Object { $build[$Matches[1]] = $Matches[3] }
        $name = $ini['avd.ini.displayname'] ? $ini['avd.ini.displayname'] : $role
        $targets.Add(@("Android $role (emulator)", (VsName $name $build['ro.build.version.release'] $build['ro.build.version.sdk']),
            "emulator-$($emulatorPort[$role])", $role))
    } else { Write-Host "No AVD named '$role' - skipping 'API + Android $role (emulator)' (create it in Android Studio's Device Manager)." }
}

# --- Visual Studio: <Sln>.slnLaunch.user ---
function Rel($file) { [System.IO.Path]::GetRelativePath($repo, $file.FullName) -replace '/', '\' }
$apiStart = [ordered]@{ Path = (Rel $api); Action = 'Start'; DebugTarget = 'https' }
$mauiStart = { param($target) [ordered]@{ Path = (Rel $maui); Action = 'Start'; DebugTarget = $target } }
$vsProfiles = @(
    [ordered]@{ Name = 'API + Web'; Projects = @($apiStart, [ordered]@{ Path = (Rel $web); Action = 'Start'; DebugTarget = 'https' }) }
    [ordered]@{ Name = 'API + Windows desktop'; Projects = @($apiStart, (& $mauiStart 'Windows Machine')) }
) + @($targets | ForEach-Object { [ordered]@{ Name = "API + $($_[0])"; Projects = @($apiStart, (& $mauiStart $_[1])) } })
$ours = @($vsProfiles | ForEach-Object Name) + @('Android phone (device)', 'Android phone (emulator)', 'Android tablet (device)', 'Android tablet (emulator)' | ForEach-Object { "API + $_" })

$slnLaunch = Join-Path $repo "$($sln.BaseName).slnLaunch.user"
$kept = (Test-Path $slnLaunch) ? @(Get-Content $slnLaunch -Raw | ConvertFrom-Json | Where-Object { $_.Name -notin $ours }) : @()
ConvertTo-Json -InputObject @($vsProfiles + $kept) -Depth 5 | Set-Content $slnLaunch

# --- VS Code: the "launch" block of .vscode/settings.json ---
# Building blocks are [bracketed] and hidden; the compounds are what you pick. "maui: Build" must stay exactly that
# (the MAUI extension runs it itself with the config's target), and `device` must be a literal adb serial because the
# extension reads it before VS Code substitutes variables.
$ws = '${workspaceFolder}'
$hidden = [ordered]@{ hidden = $true }
$winOut = "$ws/src/Maui/bin/Debug/$windowsTfm/$rid"
$configs = @(
    [ordered]@{ name = '[API]'; type = 'coreclr'; request = 'launch'; preLaunchTask = 'build: api'
        program = "$ws/src/Api/bin/Debug/$apiTfm/$($api.BaseName).dll"; cwd = "$ws/src/Api"
        env = [ordered]@{ ASPNETCORE_ENVIRONMENT = 'Development'; ASPNETCORE_URLS = ($apiUrls -join ';') }; presentation = $hidden }
    [ordered]@{ name = '[Web]'; type = 'blazorwasm'; request = 'launch'; preLaunchTask = 'build: web'
        cwd = "$ws/src/Web"; url = $webUrl; browser = 'chrome'; presentation = $hidden }
    [ordered]@{ name = '[Windows desktop]'; type = 'coreclr'; request = 'launch'; preLaunchTask = 'build: windows desktop'
        program = "$winOut/$($maui.BaseName).exe"; cwd = $winOut; presentation = $hidden }
) + @($targets | ForEach-Object {
    [ordered]@{ name = "[$($_[0])]"; type = 'maui'; request = 'launch'; preLaunchTask = 'maui: Build'
        targetFramework = $androidTfm; platform = 'android'; device = $_[2]; presentation = $hidden } })
$compounds = @(
    [ordered]@{ name = 'API + Web'; configurations = @('[API]', '[Web]') }
    [ordered]@{ name = 'API + Windows desktop'; configurations = @('[API]', '[Windows desktop]') }
) + @($targets | ForEach-Object {
    $c = [ordered]@{ name = "API + $($_[0])"; configurations = @('[API]', "[$($_[0])]") }
    if ($_[3]) { $c.preLaunchTask = "android: $($_[3]) emulator" }
    $c })
$order = 0
foreach ($c in $compounds) { $order++; $c.stopAll = $true; $c.presentation = [ordered]@{ group = '1-profiles'; order = $order } }

$settingsFile = Join-Path $repo '.vscode/settings.json'
$settings = (Test-Path $settingsFile) ? (Get-Content $settingsFile -Raw | ConvertFrom-Json -AsHashtable) : [ordered]@{}
$oldLaunch = $settings['launch']
$ourConfigs = @($configs | ForEach-Object name) + @('phone', 'tablet' | ForEach-Object { "[Android $_ (device)]", "[Android $_ (emulator)]" })
$settings['launch'] = [ordered]@{
    version = '0.2.0'
    configurations = @($configs) + @($oldLaunch.configurations | Where-Object { $_ -and $_.name -notin $ourConfigs })
    compounds = @($compounds) + @($oldLaunch.compounds | Where-Object { $_ -and $_.name -notin $ours })
}
New-Item -ItemType Directory -Force (Split-Path $settingsFile) | Out-Null
@(
    '// The "launch" block is written by tools/dev-profiles.ps1 (this file is gitignored). Re-run it rather than editing'
    '// the six "API + ..." profiles or their [bracketed] building blocks; anything else here is kept.'
    (ConvertTo-Json -InputObject $settings -Depth 10)
) | Set-Content $settingsFile

Write-Host "Wrote $($vsProfiles.Count) profiles to $(Split-Path $slnLaunch -Leaf) and .vscode/settings.json:"
$vsProfiles | ForEach-Object { Write-Host "  $($_.Name)" }
