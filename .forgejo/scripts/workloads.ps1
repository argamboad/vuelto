#Requires -Version 7.0
<#
.SYNOPSIS
  Restores the MAUI workloads unless this runner already restored exactly these for this project (Env L25).

.DESCRIPTION
  `dotnet workload restore --version <set>` re-installs every workload manifest each time (~3 min on the Windows
  leg) and then finds every pack already installed. The Windows runner runs in host mode and keeps its SDK and
  workloads between runs, so after one restore the rest are no-ops that cost minutes.

  After a successful restore this stamps the SDK directory with a key of the workload set + the project file +
  global.json. The next run skips when that stamp is there AND `dotnet workload --version` still reports the set.
  A changed project, SDK or pin, or a workload changed by hand, restores again. The Linux legs run in fresh
  containers, so they never find a stamp and restore as before.

  Called by the Forgejo copy of ci.yml (native-build). The same file lives in perezosoft-platform, y-el-vuelto and
  jigger-jot.

.EXAMPLE
  pwsh .forgejo/scripts/workloads.ps1 -Project src/Maui/Perezosoft.Maui.csproj -Version 10.0.400.1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$Version
)

$ErrorActionPreference = 'Stop'

# Where the SDK lives: setup-dotnet sets DOTNET_ROOT; otherwise the folder of the dotnet on PATH (links resolved).
$root = $env:DOTNET_ROOT
if (-not $root) {
    $exe = Get-Item (Get-Command dotnet).Source
    if ($exe.LinkTarget) { $exe = Get-Item ([IO.Path]::GetFullPath($exe.LinkTarget, $exe.DirectoryName)) }
    $root = $exe.DirectoryName
}

$inputs = "$Version`n" + (Get-Content -Raw $Project) + "`n" + (Get-Content -Raw global.json)
$hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($inputs.Replace("`r`n", "`n")))
$stamp = Join-Path $root (".workload-restore-" + [Convert]::ToHexString($hash).Substring(0, 16).ToLowerInvariant())

$current = (dotnet workload --version 2>$null) | Where-Object { $_ -match '^\d+\.\d+\.\d+' } | Select-Object -First 1
if ((Test-Path $stamp) -and "$current".Trim() -eq $Version) {
    Write-Host "MAUI workloads $Version are already restored for this project on this runner ($stamp) - skipping the restore."
    exit 0
}

dotnet workload restore $Project --version $Version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

try {
    New-Item -ItemType File -Force -Path $stamp | Out-Null
    Write-Host "Stamped $stamp - the next run on this runner skips the restore while the inputs stay the same."
} catch {
    Write-Warning "Could not write $stamp ($($_.Exception.Message)); the next run restores again."
}
