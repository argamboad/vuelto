#Requires -Version 7.0
<#
.SYNOPSIS
  Holds every file that states a local port to local-ports.props, the one source (Arch A10).

.DESCRIPTION
  The repo's block of local ports lives in local-ports.props. Code reads it through the generated LocalPorts class
  (Directory.Build.props); the files that cannot read MSBuild are rewritten by this script: the docker-compose
  defaults, both launch profiles, src/Api/appsettings.Development.json, src/Web/wwwroot/appsettings.json,
  .env.example, tests/E2E.Tests/playwright.runsettings, the Postman local environment and this repo's row in the
  README's "Local ports" table. LocalPortsTests runs the same comparison in CI and fails on any disagreement.

  Without -Apply the script only reports: exit 0 when every site agrees, exit 1 with the list of files that would
  change. A downstream app picks its block by editing local-ports.props and running this once with -Apply.

.PARAMETER Apply
  Rewrite the files that disagree. Without it, report only.

.EXAMPLE
  pwsh tools/ports.ps1
  pwsh tools/ports.ps1 -Apply
#>
[CmdletBinding()]
param(
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# --- the block -----------------------------------------------------------------------------------------------------
$props = [xml](Get-Content (Join-Path $root 'local-ports.props') -Raw)
function Port([string]$name) {
    $node = $props.Project.PropertyGroup.$name
    if (-not $node) { throw "local-ports.props has no <$name>" }
    [string]$node
}
$block = @{
    Db       = Port 'LocalDbPort'
    Smtp     = Port 'LocalSmtpPort'
    MailUi   = Port 'LocalMailUiPort'
    App      = Port 'LocalAppPort'
    ApiHttps = Port 'LocalApiHttpsPort'
    ApiHttp  = Port 'LocalApiHttpPort'
    WebHttps = Port 'LocalWebHttpsPort'
    WebHttp  = Port 'LocalWebHttpPort'
}

# --- the derived sites: (relative path, rewrite function over the file's text) --------------------------------------
$sites = [ordered]@{
    'docker-compose.yml' = {
        param($t)
        $t = [regex]::Replace($t, '\$\{DB_PORT:-\d+\}', ('${DB_PORT:-' + $block.Db + '}'))
        $t = [regex]::Replace($t, '\$\{MAIL_SMTP_PORT:-\d+\}', ('${MAIL_SMTP_PORT:-' + $block.Smtp + '}'))
        $t = [regex]::Replace($t, '\$\{MAIL_UI_PORT:-\d+\}', ('${MAIL_UI_PORT:-' + $block.MailUi + '}'))
        [regex]::Replace($t, '\$\{APP_PORT:-\d+\}', ('${APP_PORT:-' + $block.App + '}'))
    }
    'src/Api/Properties/launchSettings.json' = {
        param($t)
        $t = [regex]::Replace($t, 'https://localhost:\d+', "https://localhost:$($block.ApiHttps)")
        [regex]::Replace($t, 'http://localhost:\d+', "http://localhost:$($block.ApiHttp)")
    }
    'src/Web/Properties/launchSettings.json' = {
        param($t)
        $t = [regex]::Replace($t, 'https://localhost:\d+', "https://localhost:$($block.WebHttps)")
        [regex]::Replace($t, 'http://localhost:\d+', "http://localhost:$($block.WebHttp)")
    }
    'src/Api/appsettings.Development.json' = {
        param($t)
        $t = [regex]::Replace($t, 'https://localhost:\d+', "https://localhost:$($block.WebHttps)")
        $t = [regex]::Replace($t, 'http://localhost:\d+', "http://localhost:$($block.WebHttp)")
        [regex]::Replace($t, '("Port":\s*)\d+', ('${1}' + $block.Smtp))
    }
    'src/Web/wwwroot/appsettings.json' = {
        param($t)
        [regex]::Replace($t, '("ApiBaseUrl":\s*"https://localhost:)\d+', ('${1}' + $block.ApiHttps))
    }
    '.env.example' = {
        param($t)
        $t = [regex]::Replace($t, '(?m)^DB_PORT=\d+', "DB_PORT=$($block.Db)")
        $t = [regex]::Replace($t, '(?m)^MAIL_SMTP_PORT=\d+', "MAIL_SMTP_PORT=$($block.Smtp)")
        $t = [regex]::Replace($t, '(?m)^MAIL_UI_PORT=\d+', "MAIL_UI_PORT=$($block.MailUi)")
        $t = [regex]::Replace($t, '(?m)^APP_PORT=\d+', "APP_PORT=$($block.App)")
        $t = [regex]::Replace($t, '(Host=localhost;Port=)\d+', ('${1}' + $block.Db))
        $t = [regex]::Replace($t, '(Auth__AppBaseUrl=https://localhost:)\d+', ('${1}' + $block.WebHttps))
        $t = [regex]::Replace($t, '(Mailpit trap at localhost:)\d+', ('${1}' + $block.Smtp))
        [regex]::Replace($t, '(https://localhost:)\d+(/api/billing/webhook)', ('${1}' + $block.ApiHttps + '${2}'))
    }
    'tests/E2E.Tests/playwright.runsettings' = {
        param($t)
        [regex]::Replace($t, 'https://localhost:\d+', "https://localhost:$($block.WebHttps)")
    }
    'README.md' = {
        param($t)
        $row = "| 1 | $($block.Db) | $($block.Smtp) | $($block.MailUi) | $($block.ApiHttps) | $($block.ApiHttp) | $($block.WebHttps) | $($block.WebHttp) | $($block.App) |"
        # [^\r\n]* rather than .*$ so a CRLF file keeps its carriage return.
        [regex]::Replace($t, '(?m)^\|(\s*\*\*[^|]*\(this repo\)[^|]*)\|[^\r\n]*', { param($m) $row.Replace('| 1 |', '|' + $m.Groups[1].Value + '|') })
    }
}
# The Postman local environment: whatever the app named it.
Get-ChildItem (Join-Path $root 'docs/postman') -Filter '*.local.postman_environment.json' | ForEach-Object {
    $rel = [System.IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/')
    $sites[$rel] = {
        param($t)
        $t = [regex]::Replace($t, '("key":\s*"baseUrl",\s*"value":\s*"https://localhost:)\d+', ('${1}' + $block.ApiHttps))
        [regex]::Replace($t, '("key":\s*"mailpitUrl",\s*"value":\s*"http://localhost:)\d+', ('${1}' + $block.MailUi))
    }
}

# --- check or apply ------------------------------------------------------------------------------------------------
$changed = @()
foreach ($rel in $sites.Keys) {
    $path = Join-Path $root $rel
    if (-not (Test-Path $path)) { throw "$rel is missing" }
    $before = Get-Content $path -Raw
    $after = & $sites[$rel] $before
    if ($after -ne $before) {
        $changed += $rel
        if ($Apply) {
            [System.IO.File]::WriteAllText($path, $after, [System.Text.UTF8Encoding]::new($false))
            Write-Host "rewrote  $rel"
        } else {
            Write-Host "differs  $rel"
        }
    }
}

if ($changed.Count -eq 0) {
    Write-Host 'Every derived site agrees with local-ports.props.'
    exit 0
}
if ($Apply) {
    Write-Host "$($changed.Count) file(s) rewritten from local-ports.props. Run the tests: LocalPortsTests holds the rest."
    exit 0
}
Write-Host "$($changed.Count) file(s) disagree with local-ports.props - run 'pwsh tools/ports.ps1 -Apply'."
exit 1
