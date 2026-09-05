<#
.SYNOPSIS
  One-command setup of the two-role RLS posture on a Neon (or any Postgres) database — runbook §7.

.DESCRIPTION
  Runs the repo's docker/db/provision-rls-runtime-role.sql as the database OWNER through the local
  Postgres container's psql (nothing to install), sets a real password on the app_runtime role, proves
  the role can read but cannot create tables, and prints the two connection strings to paste into the
  host (Render): the runtime one for ConnectionStrings__DefaultConnection and the owner one for
  ConnectionStrings__Migrations. Idempotent: safe to run again.

  Secrets stay on your machine: nothing is written to disk and nothing leaves except to your database.

.PARAMETER OwnerUrl
  The OWNER connection as a URL, as Neon's "Connect" dialog shows it (pooling OFF), e.g.
  postgresql://neondb_owner:npg_xxx@ep-xxx.us-west-2.aws.neon.tech/neondb?sslmode=require

.PARAMETER RuntimePassword
  The new password for the app_runtime role (24+ random characters). Avoid the characters ' ; = for
  a painless paste into a key=value connection string.

.PARAMETER Container
  The local Postgres container whose psql is used. Default: vuelto-db-1 (the compose stack).

.EXAMPLE
  .\tools\staging-rls.ps1 -OwnerUrl "postgresql://neondb_owner:npg_...@ep-....aws.neon.tech/neondb?sslmode=require" -RuntimePassword "Xk9...24chars..."
#>
param(
    [Parameter(Mandatory = $true)] [string] $OwnerUrl,
    [Parameter(Mandatory = $true)] [string] $RuntimePassword,
    [string] $Container = "vuelto-db-1"
)
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false   # exit codes are checked by hand below (step 4 EXPECTS a failure)
$repo = Split-Path -Parent $PSScriptRoot
$script = Join-Path $repo "docker\db\provision-rls-runtime-role.sql"
if (-not (Test-Path $script)) { throw "Provisioning script not found: $script" }
if ($RuntimePassword -match "['`";=]") { throw "Pick a runtime password without the characters ' ; = or double quotes (they break the key=value connection string)." }

$uri = [Uri] $OwnerUrl
if ($uri.Scheme -notin @("postgresql", "postgres")) { throw "OwnerUrl must start with postgresql://" }
$ownerUser, $ownerPassword = $uri.UserInfo.Split(":", 2) | ForEach-Object { [Uri]::UnescapeDataString($_) }
$db = $uri.AbsolutePath.TrimStart("/")
$port = if ($uri.Port -gt 0) { $uri.Port } else { 5432 }
$runtimeUrl = "postgresql://app_runtime:$([Uri]::EscapeDataString($RuntimePassword))@$($uri.Host):$port/$db$($uri.Query)"

function Invoke-Psql([string] $url, [string] $sql) {
    $out = & docker exec -i $Container psql $url -v ON_ERROR_STOP=1 -At -c $sql 2>&1
    if ($LASTEXITCODE -ne 0) { throw "psql failed:`n$out" }
    return $out
}

Write-Host "1/4  Provisioning the app_runtime role (as $ownerUser)..." -ForegroundColor Cyan
$out = Get-Content $script -Raw | & docker exec -i $Container psql $OwnerUrl -v ON_ERROR_STOP=1 -q -f - 2>&1
if ($LASTEXITCODE -ne 0) { throw "Provisioning failed:`n$out" }
Write-Host "     role + grants in place." -ForegroundColor Green

Write-Host "2/4  Setting the runtime password..." -ForegroundColor Cyan
Invoke-Psql $OwnerUrl "ALTER ROLE app_runtime PASSWORD '$RuntimePassword';" | Out-Null
Write-Host "     done." -ForegroundColor Green

Write-Host "3/4  Proving app_runtime can read..." -ForegroundColor Cyan
$months = Invoke-Psql $runtimeUrl 'SELECT count(*) FROM "Months";'
Write-Host "     reads OK (Months rows visible to the role: $($months.Trim()))." -ForegroundColor Green

Write-Host "4/4  Proving app_runtime is fenced (must FAIL to create a table)..." -ForegroundColor Cyan
$denied = & docker exec -i $Container psql $runtimeUrl -At -c "CREATE TABLE should_fail (id int);" 2>&1
if ($LASTEXITCODE -eq 0) {
    & docker exec -i $Container psql $OwnerUrl -At -c "DROP TABLE IF EXISTS should_fail;" | Out-Null
    throw "app_runtime was able to CREATE TABLE — it is privileged; check the role on Neon (Roles → app_runtime) before pointing the app at it."
}
Write-Host "     fenced: $($denied | Select-Object -First 1)" -ForegroundColor Green

$ownerKv   = "Host=$($uri.Host);Port=$port;Database=$db;Username=$ownerUser;Password=$ownerPassword;SSL Mode=Require;Trust Server Certificate=true"
$runtimeKv = "Host=$($uri.Host);Port=$port;Database=$db;Username=app_runtime;Password=$RuntimePassword;SSL Mode=Require;Trust Server Certificate=true"

Write-Host ""
Write-Host "Paste these into the host's environment (Render -> service -> Environment), then Save, rebuild and deploy:" -ForegroundColor Yellow
Write-Host ""
Write-Host "ConnectionStrings__DefaultConnection" -ForegroundColor White
Write-Host "  $runtimeKv"
Write-Host "ConnectionStrings__Migrations" -ForegroundColor White
Write-Host "  $ownerKv"
Write-Host "Rls__EnforceRuntimeRole" -ForegroundColor White
Write-Host "  true"
Write-Host ""
Write-Host "The app then refuses to boot if its runtime connection could bypass RLS; the migrations run on the owner connection. Household restores (tools/snapshot-household.sql) keep using the OWNER connection." -ForegroundColor DarkGray
exit 0   # step 4's expected psql failure would otherwise leak as the script's exit code
