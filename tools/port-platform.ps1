#Requires -Version 7.0
<#
.SYNOPSIS
  Ports the platform's changes into this app: a three-way merge per file from the stamped platform commit to a target,
  then the manifest and the stamp the ownership gate reads (Arch A2, R160).

.DESCRIPTION
  This repo is a clone-and-rebrand of perezosoft-platform (ADR-019). platform-stamp.json names the platform commit it is
  synced to and the brand map (platform token -> this repo's token). For every file the platform's ownership map
  (platform-ownership.json at the target commit) classes as `platform` or `adapts`, the script takes the platform's
  version at the stamp as the base and at the target as theirs, renames the brand in both, and merges them into this
  repo's copy with `git merge-file --diff3` (conflict markers are left for a human; `app` files are never touched;
  `sample` files are left alone). Then it writes tests/Api.Tests/App/platform-manifest.json (the brand-normalised hash
  of every platform and adapts file at the target, with its class) and platform-stamp.json (the target commit and the
  date). PlatformOwnershipTests holds this repo to those two files: a platform-class file that differs from the manifest
  fails unless listed in tests/Api.Tests/App/PlatformDivergences.json with a reason.

  Without -Apply the script prints the plan (added / same / clean merge / CONFLICT / binary / deleted upstream) and
  writes nothing.

.PARAMETER Platform
  Path to a perezosoft-platform checkout (any branch; the script reads commits, not the working tree).

.PARAMETER To
  The platform commit (or branch) to port to. Default: develop.

.PARAMETER Apply
  Write the merged files, the manifest and the stamp. Without it, report only.

.PARAMETER Repo
  This app's repo root. Default: the parent of tools/.

.EXAMPLE
  pwsh tools/port-platform.ps1 -Platform ..\perezosoft-platform
  pwsh tools/port-platform.ps1 -Platform ..\perezosoft-platform -To 0fd0d76 -Apply
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Platform,
    [string]$To = 'develop',
    [switch]$Apply,
    [string]$Repo = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$Platform = (Resolve-Path $Platform).Path
$Repo = (Resolve-Path $Repo).Path

# --- git plumbing, binary-safe ------------------------------------------------------------------------------------------
function Git-Text([string]$repo, [string[]]$arguments) {
    $out = & git -C $repo @arguments 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return ($out -join "`n")
}
function Git-Bytes([string]$repo, [string]$spec) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new('git')
    foreach ($a in @('-C', $repo, 'cat-file', 'blob', $spec)) { $psi.ArgumentList.Add($a) }
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.UseShellExecute = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    $ms = [System.IO.MemoryStream]::new()
    $p.StandardOutput.BaseStream.CopyTo($ms)
    $p.StandardError.ReadToEnd() | Out-Null
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) { return $null }
    return $ms.ToArray()
}

# --- the stamp, the brand map, the target ----------------------------------------------------------------------------------
$stampPath = Join-Path $Repo 'platform-stamp.json'
if (-not (Test-Path $stampPath)) { throw "$stampPath is missing: this repo carries no platform stamp" }
# -AsHashtable: the brand map's keys differ only by case ("Vuelto", "vuelto"), which a PSObject cannot hold.
$stamp = Get-Content $stampPath -Raw | ConvertFrom-Json -AsHashtable
$from = $stamp['commit']
# A case-SENSITIVE map: [ordered]@{} would fold "Vuelto" and "vuelto" into one key.
$brand = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::Ordinal)
foreach ($k in $stamp['brand'].Keys) { $brand[$k] = [string]$stamp['brand'][$k] }
$platformTokens = @($brand.Keys)
$appTokens = @($brand.Values)
$target = Git-Text $Platform @('rev-parse', '--verify', "$To^{commit}")
if (-not $target) { throw "'$To' is not a commit in $Platform" }
if ($from -and $from -eq $target) { Write-Host "Already at platform $target. Nothing to port."; exit 0 }

# The platform repo's own name ("perezosoft-platform") carries the brand token but is a name, not the brand: it is kept,
# or every file that names the platform would read "<app>-platform" downstream.
$keepName = ([string]$stamp['platform']).Split('/')[-1]
function Rebrand([string]$text) {
    $guard = [string][char]0xE000
    if ($keepName) { $text = $text.Replace($keepName, $guard) }
    foreach ($k in ($brand.Keys | Sort-Object { $_.Length } -Descending)) { $text = $text.Replace($k, [string]$brand[$k]) }
    if ($keepName) { $text = $text.Replace($guard, $keepName) }
    return $text
}

# --- the ownership map at the target ------------------------------------------------------------------------------------
function Glob-ToRegex([string]$glob) {
    $sb = [System.Text.StringBuilder]::new('^')
    for ($i = 0; $i -lt $glob.Length; $i++) {
        if ($i + 2 -lt $glob.Length -and $glob.Substring($i, 3) -eq '**/') { [void]$sb.Append('(?:.*/)?'); $i += 2; continue }
        if ($i + 1 -lt $glob.Length -and $glob.Substring($i, 2) -eq '**') { [void]$sb.Append('.*'); $i += 1; continue }
        $c = $glob[$i]
        if ($c -eq '*') { [void]$sb.Append('[^/]*') } elseif ($c -eq '?') { [void]$sb.Append('[^/]') } else { [void]$sb.Append([regex]::Escape([string]$c)) }
    }
    return [regex]::new($sb.Append('$').ToString())
}
$mapJson = Git-Text $Platform @('show', "${target}:platform-ownership.json")
if (-not $mapJson) { throw "the platform at $target has no platform-ownership.json (it predates Arch A2)" }
$rules = @(($mapJson | ConvertFrom-Json).rules | ForEach-Object { [pscustomobject]@{ Regex = (Glob-ToRegex $_.glob); Class = $_.class } })
function Classify([string]$path) {
    foreach ($r in $rules) { if ($r.Regex.IsMatch($path)) { return $r.Class } }
    return $null
}

# --- the hash both sides compare (PlatformOwnership.NormalizedHash in C#; keep the two identical) -------------------------
function Normalized-Hash([byte[]]$bytes, [string[]]$tokens) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $probe = [Math]::Min(8000, $bytes.Length)
    for ($i = 0; $i -lt $probe; $i++) { if ($bytes[$i] -eq 0) { return ([System.Convert]::ToHexString($sha.ComputeHash($bytes))).ToLowerInvariant() } }
    $start = 0
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { $start = 3 }
    $text = [System.Text.Encoding]::UTF8.GetString($bytes, $start, $bytes.Length - $start).Replace("`r`n", "`n")
    foreach ($t in ($tokens | Sort-Object { $_.Length } -Descending)) {
        $ph = if ([char]::IsUpper($t[0])) { '{BRAND}' } else { '{brand}' }
        $text = $text.Replace($t, $ph)
    }
    $lines = $text.Split("`n") | ForEach-Object { $_.TrimEnd(' ', "`t") }
    $text = ([string]::Join("`n", $lines)).TrimEnd("`n", ' ', "`t")
    return ([System.Convert]::ToHexString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($text)))).ToLowerInvariant()
}
function Bytes-Equal([byte[]]$a, [byte[]]$b) {
    if ($null -eq $a -or $null -eq $b) { return $false }
    return ([System.Convert]::ToHexString($a)) -eq ([System.Convert]::ToHexString($b))
}
function Is-Binary([byte[]]$bytes) {
    $probe = [Math]::Min(8000, $bytes.Length)
    for ($i = 0; $i -lt $probe; $i++) { if ($bytes[$i] -eq 0) { return $true } }
    return $false
}
function Decode([byte[]]$bytes) {
    $start = 0
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { $start = 3 }
    return [System.Text.Encoding]::UTF8.GetString($bytes, $start, $bytes.Length - $start).Replace("`r`n", "`n")
}

# --- the plan ----------------------------------------------------------------------------------------------------------
$targetFiles = (Git-Text $Platform @('ls-tree', '-r', '--name-only', $target)) -split "`n" | Where-Object { $_ }
$fromFiles = if ($from) { (Git-Text $Platform @('ls-tree', '-r', '--name-only', $from)) -split "`n" | Where-Object { $_ } } else { @() }
$plan = [System.Collections.Generic.List[object]]::new()
$manifest = [System.Collections.Generic.List[object]]::new()
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("port-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    foreach ($path in $targetFiles) {
        $class = Classify $path
        if (-not $class) { $plan.Add([pscustomobject]@{ Verb = 'UNCLASSIFIED'; Path = $path }); continue }
        if ($class -eq 'app') { continue }
        $theirs = Git-Bytes $Platform "${target}:$path"
        if ($class -in @('platform', 'adapts')) {
            # Hashed as this repo holds it: the platform's text after the rename, normalised with this repo's tokens. The
            # platform's own text normalised with the platform's tokens could never match where it already names this app.
            $asHeld = if (Is-Binary $theirs) { $theirs } else { [System.Text.Encoding]::UTF8.GetBytes((Rebrand (Decode $theirs))) }
            $manifest.Add([ordered]@{ path = $path; class = $class; hash = (Normalized-Hash $asHeld $appTokens) })
        }
        if ($class -eq 'sample') { continue }
        $local = Rebrand $path
        $localFull = Join-Path $Repo $local
        $ours = if (Test-Path $localFull) { [System.IO.File]::ReadAllBytes($localFull) } else { $null }
        $base = if ($from -and ($fromFiles -contains $path)) { Git-Bytes $Platform "${from}:$path" } else { $null }

        if (Is-Binary $theirs) {
            if ($null -eq $ours) { $plan.Add([pscustomobject]@{ Verb = 'added'; Path = $local; Bytes = $theirs }); continue }
            if (Bytes-Equal $ours $theirs) { $plan.Add([pscustomobject]@{ Verb = 'same'; Path = $local }); continue }
            if (Bytes-Equal $ours $base) { $plan.Add([pscustomobject]@{ Verb = 'clean'; Path = $local; Bytes = $theirs }); continue }
            $plan.Add([pscustomobject]@{ Verb = 'BINARY'; Path = $local }); continue
        }

        $theirsText = Rebrand (Decode $theirs)
        if ($null -eq $ours) { $plan.Add([pscustomobject]@{ Verb = 'added'; Path = $local; Text = $theirsText; Crlf = $false; Bom = $false }); continue }
        $crlf = [System.Text.Encoding]::UTF8.GetString($ours).Contains("`r`n")
        $bom = $ours.Length -ge 3 -and $ours[0] -eq 0xEF -and $ours[1] -eq 0xBB -and $ours[2] -eq 0xBF
        $oursText = Decode $ours
        if ($oursText -eq $theirsText) { $plan.Add([pscustomobject]@{ Verb = 'same'; Path = $local }); continue }
        $baseText = if ($null -ne $base) { Rebrand (Decode $base) } else { '' }
        $fo = Join-Path $tmp 'ours'; $fb = Join-Path $tmp 'base'; $ft = Join-Path $tmp 'theirs'
        [System.IO.File]::WriteAllText($fo, $oursText, [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText($fb, $baseText, [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText($ft, $theirsText, [System.Text.UTF8Encoding]::new($false))
        & git merge-file --diff3 -L app -L platform-before -L platform $fo $fb $ft 2>$null | Out-Null
        $conflicts = $LASTEXITCODE
        $merged = [System.IO.File]::ReadAllText($fo)
        if ($conflicts -lt 0) { throw "git merge-file failed on $local" }
        $verb = if ($conflicts -gt 0) { "CONFLICT x$conflicts" } elseif ($merged -eq $oursText) { 'no-op' } else { 'clean' }
        if ($null -eq $base -and $conflicts -gt 0) { $verb = "CONFLICT (no base: first port)" }
        $plan.Add([pscustomobject]@{ Verb = $verb; Path = $local; Text = $merged; Crlf = $crlf; Bom = $bom })
    }
    foreach ($gone in ($fromFiles | Where-Object { $targetFiles -notcontains $_ })) {
        $class = Classify $gone
        if ($class -in @('platform', 'adapts')) { $plan.Add([pscustomobject]@{ Verb = 'deleted upstream'; Path = (Rebrand $gone) }) }
    }
}
finally { try { Remove-Item -Recurse -Force $tmp -ErrorAction Stop } catch { <# a scratch dir; the OS temp sweep gets it #> } }

# --- report, then apply ----------------------------------------------------------------------------------------------
$counts = $plan | Group-Object { ($_.Verb -split ' ')[0] } | ForEach-Object { "$($_.Name)=$($_.Count)" }
Write-Host ("Port {0} -> {1}: {2}" -f ($(if ($from) { $from.Substring(0, 7) } else { '(no stamp)' })), $target.Substring(0, 7), ($counts -join ', '))
foreach ($item in ($plan | Where-Object { $_.Verb -ne 'same' -and $_.Verb -ne 'no-op' })) { Write-Host ("  {0,-30} {1}" -f $item.Verb, $item.Path) }
if (-not $Apply) {
    Write-Host 'Dry run: nothing written. Add -Apply to merge, and to write the manifest and the stamp.'
    exit 0
}

foreach ($item in $plan) {
    if ($item.Verb -in @('same', 'no-op', 'BINARY', 'UNCLASSIFIED', 'deleted upstream')) { continue }
    $full = Join-Path $Repo $item.Path
    New-Item -ItemType Directory -Force (Split-Path -Parent $full) | Out-Null
    if ($item.PSObject.Properties.Name -contains 'Bytes' -and $null -ne $item.Bytes) {
        [System.IO.File]::WriteAllBytes($full, $item.Bytes)
    } else {
        $text = if ($item.Crlf) { $item.Text.Replace("`n", "`r`n") } else { $item.Text }
        [System.IO.File]::WriteAllText($full, $text, [System.Text.UTF8Encoding]::new([bool]$item.Bom))
    }
}

$manifestPath = Join-Path $Repo 'tests/Api.Tests/App/platform-manifest.json'
New-Item -ItemType Directory -Force (Split-Path -Parent $manifestPath) | Out-Null
$manifestDoc = [ordered]@{
    '$comment' = @(
        'Written by tools/port-platform.ps1 (Arch A2): the brand-normalised hash of every platform and adapts file at the',
        'platform commit this repo is synced to. PlatformOwnershipTests compares this repo against it. Do not edit by hand.')
    platform = [string]$stamp['platform']
    commit = $target
    files = @($manifest | Sort-Object { $_.path })
}
[System.IO.File]::WriteAllText($manifestPath, ($manifestDoc | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
$stampDoc = [ordered]@{}
if ($stamp.ContainsKey('$comment')) { $stampDoc['$comment'] = $stamp['$comment'] }
$stampDoc['platform'] = [string]$stamp['platform']
$stampDoc['commit'] = $target
$stampDoc['date'] = (Get-Date).ToString('yyyy-MM-dd')
$stampDoc['brand'] = $brand
[System.IO.File]::WriteAllText($stampPath, ($stampDoc | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))

$conflicted = @($plan | Where-Object { $_.Verb -like 'CONFLICT*' -or $_.Verb -eq 'BINARY' -or $_.Verb -eq 'deleted upstream' })
Write-Host ("Wrote {0} file(s), the manifest ({1} entries) and the stamp ({2})." -f (@($plan | Where-Object { $_.Verb -in @('added', 'clean') -or $_.Verb -like 'CONFLICT*' }).Count), $manifest.Count, $target.Substring(0, 7))
if ($conflicted.Count -gt 0) {
    Write-Host ("{0} file(s) need a hand: resolve the <<<<<<< markers, copy the binaries, delete what the platform deleted." -f $conflicted.Count)
}
Write-Host 'Then build and run the tests: PlatformOwnershipTests holds this repo to the manifest.'
exit 0
