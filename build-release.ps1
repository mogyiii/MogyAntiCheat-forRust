<#
.SYNOPSIS
    Produces a release-ready copy of MogyAntiCheat.cs with the weekly-report webhook injected.

.DESCRIPTION
    The public source keeps DefaultWeeklyReportWebhook = "__WEEKLY_WEBHOOK__" (a sentinel), so the
    real webhook is never committed. This script writes an injected copy to build\MogyAntiCheat.cs
    with the sentinel replaced by your real Discord webhook URL. Compile THAT copy into the release
    DLL (see docs/DLL_BUILD.md). The tracked source file is never modified.

    Webhook source (first match wins):
      1. -Webhook parameter
      2. $env:MOGYAC_WEEKLY_WEBHOOK
      3. webhook.secret file next to this script (gitignored)

.EXAMPLE
    .\build-release.ps1 -Webhook "https://discord.com/api/webhooks/xxx/yyy"

.EXAMPLE
    $env:MOGYAC_WEEKLY_WEBHOOK = "https://discord.com/api/webhooks/xxx/yyy"; .\build-release.ps1
#>
[CmdletBinding()]
param(
    [string]$Webhook,
    [string]$Source = "MogyAntiCheat.cs",
    [string]$OutDir = "build"
)

$ErrorActionPreference = "Stop"
$Sentinel = "__WEEKLY_WEBHOOK__"

# Resolve the webhook from param / env / secret file.
if ([string]::IsNullOrWhiteSpace($Webhook)) { $Webhook = $env:MOGYAC_WEEKLY_WEBHOOK }
if ([string]::IsNullOrWhiteSpace($Webhook) -and (Test-Path "webhook.secret")) {
    $Webhook = (Get-Content "webhook.secret" -Raw).Trim()
}
if ([string]::IsNullOrWhiteSpace($Webhook)) {
    throw "No webhook provided. Use -Webhook, set MOGYAC_WEEKLY_WEBHOOK, or create webhook.secret."
}
if (-not $Webhook.StartsWith("http")) {
    throw "Webhook does not look like a URL: $Webhook"
}

if (-not (Test-Path $Source)) { throw "Source file not found: $Source" }

$content = Get-Content $Source -Raw
if ($content -notmatch [regex]::Escape($Sentinel)) {
    throw "Sentinel '$Sentinel' not found in $Source. Is the source already modified?"
}

$injected = $content.Replace($Sentinel, $Webhook)

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
$outFile = Join-Path $OutDir "MogyAntiCheat.cs"
# Write UTF-8 without BOM so the compiler is happy.
[System.IO.File]::WriteAllText((Resolve-Path $OutDir | ForEach-Object { Join-Path $_ "MogyAntiCheat.cs" }), $injected, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Injected webhook into $outFile" -ForegroundColor Green
Write-Host "Now compile $outFile into MogyAntiCheat.dll (see docs/DLL_BUILD.md)." -ForegroundColor Cyan
Write-Host "Do NOT commit the build\ folder or webhook.secret (already gitignored)." -ForegroundColor Yellow
