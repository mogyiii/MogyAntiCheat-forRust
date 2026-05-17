# =============================================================================
# MogyAntiCheat — Deploy szkript (PowerShell)
# =============================================================================
# Használat:
#   .\deploy.ps1              → teljes deploy (plugin + lang fájlok)
#   .\deploy.ps1 -PluginOnly  → csak a .cs fájl
#   .\deploy.ps1 -LangOnly    → csak a lang fájlok
#   .\deploy.ps1 -DryRun      → mit tenne, de nem csinál semmit
# =============================================================================

param(
    [switch]$PluginOnly,
    [switch]$LangOnly,
    [switch]$DryRun
)

# =============================================================================
# ⚙️  KONFIGURÁCIÓ — Csak ezt a részt kell szerkesztened
# =============================================================================

$SSH_HOST    = "rustszerver"        # SSH config neve VAGY user@ip (pl: ubuntu@1.2.3.4)
$SSH_PORT    = 22
$SSH_KEY     = ""                   # Ha üres, alapértelmezett kulcs. Pl: "C:\Users\te\.ssh\id_ed25519"

$RUNTIME     = "oxide"              # "oxide" vagy "carbon"

$OXIDE_PLUGINS_DIR  = "/home/rust/server/oxide/plugins"
$OXIDE_LANG_DIR     = "/home/rust/server/oxide/lang"
$CARBON_PLUGINS_DIR = "/home/rust/server/carbon/plugins"
$CARBON_LANG_DIR    = "/home/rust/server/carbon/lang"

$RELOAD_CMD  = "echo 'oxide.reload MogyAntiCheat' | tee /proc/1/fd/0 || true"
$BACKUP_DIR  = "/home/rust/backups/mogyac"

$LOCAL_PLUGIN   = "MogyAntiCheat.cs"
$LOCAL_LANG_EN  = "oxide/lang/en/MogyAntiCheat.json"
$LOCAL_LANG_HU  = "oxide/lang/hu/MogyAntiCheat.json"

# =============================================================================

$DeployPlugin = -not $LangOnly
$DeployLang   = -not $PluginOnly

if ($RUNTIME -eq "carbon") {
    $PLUGINS_DIR = $CARBON_PLUGINS_DIR
    $LANG_DIR    = $CARBON_LANG_DIR
    $RELOAD_CMD  = "echo 'c.reload MogyAntiCheat' | tee /proc/1/fd/0 || true"
} else {
    $PLUGINS_DIR = $OXIDE_PLUGINS_DIR
    $LANG_DIR    = $OXIDE_LANG_DIR
}

$SSH_OPTS = @("-p", $SSH_PORT, "-o", "ConnectTimeout=10", "-o", "BatchMode=yes")
if ($SSH_KEY -ne "") { $SSH_OPTS += @("-i", $SSH_KEY) }

function Log    ($msg) { Write-Host "[deploy] $msg" -ForegroundColor Cyan }
function Ok     ($msg) { Write-Host "[  OK  ] $msg" -ForegroundColor Green }
function Warn   ($msg) { Write-Host "[ WARN ] $msg" -ForegroundColor Yellow }
function Err    ($msg) { Write-Host "[ERROR ] $msg" -ForegroundColor Red }
function Section($msg) { Write-Host "`n▶ $msg" -ForegroundColor Cyan -NoNewline; Write-Host "" }

function Invoke-SSH {
    param([string]$Cmd)
    if ($DryRun) { Log "[DRY] ssh ${SSH_HOST}: $Cmd"; return }
    ssh @SSH_OPTS $SSH_HOST $Cmd
}

function Invoke-SCP {
    param([string]$Src, [string]$Dst)
    if ($DryRun) { Log "[DRY] scp $Src → ${SSH_HOST}:$Dst"; return }
    scp @SSH_OPTS $Src "${SSH_HOST}:${Dst}"
}

# ---------------------------------------------------------------------------
# 1. Előfeltételek
# ---------------------------------------------------------------------------
Section "Előfeltételek ellenőrzése"

foreach ($f in @($LOCAL_PLUGIN, $LOCAL_LANG_EN, $LOCAL_LANG_HU)) {
    if (-not (Test-Path $f)) { Err "Hiányzó fájl: $f"; exit 1 }
}
Ok "Helyi fájlok megvannak"

$PluginVersion = (Select-String -Path $LOCAL_PLUGIN -Pattern '"(\d+\.\d+\.\d+)"' |
    Select-Object -First 1).Matches[0].Groups[1].Value
Log "Plugin verzió: $PluginVersion"

if (-not $DryRun) {
    Log "SSH kapcsolat tesztelése: $SSH_HOST..."
    $test = ssh @SSH_OPTS $SSH_HOST "echo OK" 2>&1
    if ($LASTEXITCODE -ne 0) {
        Err "SSH kapcsolat sikertelen: $SSH_HOST"
        Err "Ellenőrizd az SSH kulcsot és a host nevet a szkript tetején"
        exit 1
    }
    Ok "SSH kapcsolat: $SSH_HOST"
}

# ---------------------------------------------------------------------------
# 2. Biztonsági mentés
# ---------------------------------------------------------------------------
Section "Biztonsági mentés"

$Timestamp   = Get-Date -Format "yyyyMMdd_HHmmss"
$BackupPath  = "$BACKUP_DIR/$Timestamp"

Invoke-SSH "mkdir -p $BackupPath"
Invoke-SSH "[ -f $PLUGINS_DIR/MogyAntiCheat.cs ] && cp $PLUGINS_DIR/MogyAntiCheat.cs $BackupPath/MogyAntiCheat.cs || true"
Ok "Backup: $BackupPath"

Invoke-SSH "ls -dt $BACKUP_DIR/*/ 2>/dev/null | tail -n +11 | xargs rm -rf 2>/dev/null || true"

# ---------------------------------------------------------------------------
# 3. Plugin feltöltése
# ---------------------------------------------------------------------------
if ($DeployPlugin) {
    Section "Plugin feltöltése"
    Invoke-SSH "mkdir -p $PLUGINS_DIR"
    Invoke-SCP $LOCAL_PLUGIN "$PLUGINS_DIR/MogyAntiCheat.cs"
    Ok "MogyAntiCheat.cs feltöltve"
}

# ---------------------------------------------------------------------------
# 4. Lang fájlok feltöltése
# ---------------------------------------------------------------------------
if ($DeployLang) {
    Section "Lang fájlok feltöltése"
    Invoke-SSH "mkdir -p $LANG_DIR/en $LANG_DIR/hu"
    Invoke-SCP $LOCAL_LANG_EN "$LANG_DIR/en/MogyAntiCheat.json"
    Ok "en/MogyAntiCheat.json feltöltve"
    Invoke-SCP $LOCAL_LANG_HU "$LANG_DIR/hu/MogyAntiCheat.json"
    Ok "hu/MogyAntiCheat.json feltöltve"
}

# ---------------------------------------------------------------------------
# 5. Plugin reload
# ---------------------------------------------------------------------------
if ($DeployPlugin) {
    Section "Plugin újratöltése"
    Invoke-SSH $RELOAD_CMD
    Start-Sleep 2
    Ok "Reload elküldve"
}

# ---------------------------------------------------------------------------
# 6. Összefoglalás
# ---------------------------------------------------------------------------
Section "Deploy összefoglalás"

if ($DryRun) { Warn "DRY RUN mód — semmi nem lett ténylegesen végrehajtva" }

Write-Host "  Verzió:   $PluginVersion"
Write-Host "  Runtime:  $RUNTIME"
Write-Host "  Szerver:  $SSH_HOST"
Write-Host "  Plugins:  $PLUGINS_DIR"
Write-Host "  Backup:   $BackupPath"
if ($DeployPlugin) { Ok "Plugin:   feltöltve" }
if ($DeployLang)   { Ok "Lang:     feltöltve (en + hu)" }

Write-Host ""
Ok "Deploy befejezve!"
