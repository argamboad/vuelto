#Requires -Version 7.0
<#
.SYNOPSIS
  Writes this clone's start profiles for Visual Studio and VS Code into gitignored files. Run it once per clone (and
  again after adding a device or an AVD).

.DESCRIPTION
  Visual Studio (<Sln>.slnLaunch.user, the startup dropdown) gets two profiles:
      API + Web      the API and the Blazor WebAssembly client
      API + app      the API and the MAUI shell, on whatever the toolbar's Debug Target names (Windows Machine, a
                     device, an emulator)
  VS ignores a MAUI project's DebugTarget in a multi-project profile, so per-device profiles there all launched the
  last Debug Target used; the toolbar is the only reliable device picker.
  VS Code (the "launch" block of .vscode/settings.json, Run and Debug) gets six, because there the profile can carry
  the device:
      API + Web                          API + Android phone (device)    API + Android phone (emulator)
      API + Windows desktop              API + Android tablet (device)   API + Android tablet (emulator)
  Both files are gitignored, because the Android ones name the developer's own devices, which a clone has no use
  for. Everything else is read from the repo - the solution name from the .slnx, the ports from the
  launchSettings.json files, the target frameworks from the csprojs - so this works unchanged after a rebrand. The
  build and emulator tasks the VS Code profiles run are in .vscode/tasks.json.

  VS Code, once per clone: run "Select C# Startup Project" (pick the MAUI project) and "Select Launch Configuration"
  (pick any Android entry) from the Command Palette. On Windows the MAUI extension otherwise assumes the Windows
  platform, attaches the CoreCLR debugger to the Mono Android app, and the app is installed but never starts. The
  choice lives in VS Code's workspace state, which no file here can set.

  Devices come from a file outside every repo (default ~/dev-tools/devices.json):
      { "phone":  { "adb": "adb-R5CW...._adb-tls-connect._tcp" }, "tablet": { "adb": "..." } }
  "adb" is the device's serial in `adb devices`, which is what the VS Code MAUI extension targets. -Discover writes
  the file from the devices adb has connected right now. Over Wi-Fi debugging the serial is the mDNS name, which stays
  the same across reconnects as long as the device is on the same network as the PC; an IP:port serial (USB-less
  connect through a VPN) changes every time and won't keep working.
  Emulators are the AVDs named `phone` and `tablet`: VS Code's profiles boot them on fixed ports first
  (tools/android-emulator.ps1), which gives them the fixed serials emulator-5554 / emulator-5556.
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
        $model = "$(& $prop 'ro.product.manufacturer') $(& $prop 'ro.product.model')"
        if ($found[$role]) { Write-Warning "More than one $role connected; keeping $($devices[$role].adb), skipping $serial."; continue }
        $found[$role] = $true
        if ($serial -match '^\d+\.\d+\.\d+\.\d+:\d+$') { Write-Warning "$model is connected as $serial, which changes on every reconnect. Put it on the PC's Wi-Fi so adb finds it by its mDNS name." }
        $devices[$role] = [ordered]@{ adb = $serial }
        Write-Host "Found $role`: $model ($serial)"
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

# --- VS Code Android targets: (profile, adb serial, emulator AVD or $null) ---
$targets = [System.Collections.Generic.List[object]]::new()
$known = (Test-Path $DevicesFile) ? (Get-Content $DevicesFile -Raw | ConvertFrom-Json) : $null
$avdHome = $env:ANDROID_AVD_HOME ? $env:ANDROID_AVD_HOME : (Join-Path $HOME '.android/avd')
$emulatorPort = @{ phone = 5554; tablet = 5556 }
foreach ($role in 'phone', 'tablet') {
    $device = $known.$role
    if ($device.adb) { $targets.Add(@("Android $role (device)", $device.adb, $null)) }
    else { Write-Host "No $role in $DevicesFile - skipping 'API + Android $role (device)' (connect it and run with -Discover)." }

    if ($sdk -and (Test-Path (Join-Path $avdHome "$role.avd/config.ini"))) {
        $targets.Add(@("Android $role (emulator)", "emulator-$($emulatorPort[$role])", $role))
    } else { Write-Host "No AVD named '$role' - skipping 'API + Android $role (emulator)' (create it in Android Studio's Device Manager)." }
}
# Every profile name this script has ever written, so a re-run replaces them all and keeps only yours.
$ours = @('API + Web', 'API + app', 'API + Windows desktop') + @('phone', 'tablet' | ForEach-Object {
    "API + Android $_ (device)", "API + Android $_ (emulator)" })

# --- Visual Studio: <Sln>.slnLaunch.user ---
# No DebugTarget on the MAUI project: VS ignores it there and uses the toolbar's.
function Rel($file) { [System.IO.Path]::GetRelativePath($repo, $file.FullName) -replace '/', '\' }
$apiStart = [ordered]@{ Path = (Rel $api); Action = 'Start'; DebugTarget = 'https' }
$vsProfiles = @(
    [ordered]@{ Name = 'API + Web'; Projects = @($apiStart, [ordered]@{ Path = (Rel $web); Action = 'Start'; DebugTarget = 'https' }) }
    [ordered]@{ Name = 'API + app'; Projects = @($apiStart, [ordered]@{ Path = (Rel $maui); Action = 'Start' }) }
)

$slnLaunch = Join-Path $repo "$($sln.BaseName).slnLaunch.user"
$kept = (Test-Path $slnLaunch) ? @(Get-Content $slnLaunch -Raw | ConvertFrom-Json | Where-Object { $_.Name -notin $ours }) : @()
ConvertTo-Json -InputObject @($vsProfiles + $kept) -Depth 5 | Set-Content $slnLaunch

# --- VS Code: the "launch" block of .vscode/settings.json ---
# Building blocks are [bracketed] and hidden; the compounds are what you pick. "maui: Build" must stay exactly that
# (the MAUI extension runs it itself with the config's target). The extension reads `project` and `device` before
# VS Code substitutes variables, so both are literal: `device` an adb serial, `project` the csproj's absolute path
# exactly as C# Dev Kit loaded it (drive letter upper-case on Windows). Without `project` it falls back to C# Dev
# Kit's startup project and, with none set, builds the solution as "undefined|Any CPU" (MSB4126).
$ws = '${workspaceFolder}'
$mauiPath = $maui.FullName
if ($IsWindows) { $mauiPath = $mauiPath.Substring(0, 1).ToUpper() + $mauiPath.Substring(1) }
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
        project = $mauiPath; configuration = 'Debug'; targetFramework = $androidTfm; platform = 'android'; device = $_[1]
        presentation = $hidden } })
$compounds = @(
    [ordered]@{ name = 'API + Web'; configurations = @('[API]', '[Web]') }
    [ordered]@{ name = 'API + Windows desktop'; configurations = @('[API]', '[Windows desktop]') }
) + @($targets | ForEach-Object {
    $c = [ordered]@{ name = "API + $($_[0])"; configurations = @('[API]', "[$($_[0])]") }
    if ($_[2]) { $c.preLaunchTask = "android: $($_[2]) emulator" }
    $c })
$order = 0
foreach ($c in $compounds) { $order++; $c.stopAll = $true; $c.presentation = [ordered]@{ group = '1-profiles'; order = $order } }

$settingsFile = Join-Path $repo '.vscode/settings.json'
$settings = (Test-Path $settingsFile) ? (Get-Content $settingsFile -Raw | ConvertFrom-Json -AsHashtable) : [ordered]@{}
# The MAUI extension takes the launch config's `configuration` only with this on.
$settings['maui.configuration.useLaunchJsonConfigurations'] = $true
# The Java extension, if installed, indexes the Android stubs the build generates under obj/ and floods Problems
# with thousands of "cannot be resolved" errors. Its own defaults first, then the build output.
$javaExclusions = @('**/node_modules/**', '**/.metadata/**', '**/archetype-resources/**', '**/META-INF/maven/**')
$settings['java.import.exclusions'] = @(@($settings['java.import.exclusions']) + $javaExclusions + @('**/obj/**', '**/bin/**') |
    Where-Object { $_ } | Select-Object -Unique)
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
    '// the "API + ..." profiles or their [bracketed] building blocks; anything else here is kept.'
    '// Android profiles, once per clone: Command Palette -> "Select C# Startup Project" (the MAUI project), then'
    '// "Select Launch Configuration" (any Android entry). Left on Windows, the app installs but never starts.'
    (ConvertTo-Json -InputObject $settings -Depth 10)
) | Set-Content $settingsFile

Write-Host "Visual Studio ($(Split-Path $slnLaunch -Leaf)) - pick the device in the toolbar's Debug Target:"
$vsProfiles | ForEach-Object { Write-Host "  $($_.Name)" }
Write-Host 'VS Code (.vscode/settings.json):'
$compounds | ForEach-Object { Write-Host "  $($_.name)" }
if ($targets.Count) {
    Write-Host 'Once per clone in VS Code, before the Android profiles: Command Palette -> "Select C# Startup Project" (the'
    Write-Host 'MAUI project), then "Select Launch Configuration" (any Android entry).'
}
