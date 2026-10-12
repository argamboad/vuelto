#Requires -Version 7.0
<#
.SYNOPSIS
  Starts (or reuses) this machine's shared Aspire Dashboard - local logs, traces and metrics for all three apps.

.DESCRIPTION
  One container for the machine, not one per repo: OTLP (gRPC) on localhost:4317, the UI on http://localhost:18888.
  Each app exports to it when its .env sets
      OpenTelemetry__Otlp__Endpoint=http://localhost:4317
  and they show up side by side, told apart by service name. Nothing to sign up for; the dashboard keeps data in
  memory and forgets it when the container restarts. Running this again is harmless - it reuses the container.
  The same file lives in perezosoft-platform, y-el-vuelto and jigger-jot. Local Dev Alignment L13.

.PARAMETER Stop
  Remove the container.

.EXAMPLE
  pwsh tools/telemetry.ps1
  pwsh tools/telemetry.ps1 -Stop
#>
[CmdletBinding()]
param([switch]$Stop)

$ErrorActionPreference = 'Stop'
$name = 'aspire-dashboard'
$image = 'mcr.microsoft.com/dotnet/aspire-dashboard:13.5.2'   # pinned, never :latest (v3 DEP-9)

if ($Stop) {
    docker rm -f $name 2>$null | Out-Null
    Write-Host "Removed $name."
    return
}

$running = docker inspect -f '{{.State.Running}}' $name 2>$null
if ($LASTEXITCODE -eq 0 -and $running -eq 'true') {
    Write-Host "$name is already running."
} elseif ($LASTEXITCODE -eq 0) {
    docker start $name | Out-Null
    Write-Host "Started the existing $name container."
} else {
    # 18889 is the dashboard's own OTLP gRPC port; publish it as the standard 4317 so .env uses the usual endpoint.
    docker run -d --name $name --restart unless-stopped `
        -p 18888:18888 -p 4317:18889 `
        -e DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true `
        $image | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "docker run failed (is Docker running?)" }
    Write-Host "Started $name ($image)."
}
Write-Host 'UI:   http://localhost:18888'
Write-Host 'OTLP: http://localhost:4317   (.env: OpenTelemetry__Otlp__Endpoint=http://localhost:4317)'
