#!/usr/bin/env bash
# =============================================================================
# MogyAntiCheat — Deploy szkript
# =============================================================================
# Használat:
#   ./deploy.sh              → teljes deploy (plugin + lang fájlok)
#   ./deploy.sh --plugin     → csak a .cs fájl
#   ./deploy.sh --lang       → csak a lang fájlok
#   ./deploy.sh --dry-run    → mit tenne, de nem csinál semmit
# =============================================================================

set -euo pipefail

# =============================================================================
# ⚙️  KONFIGURÁCIÓ — Csak ezt a részt kell szerkesztened
# =============================================================================

SSH_HOST="rustszerver"            # SSH config neve VAGY user@ip (pl: ubuntu@1.2.3.4)
SSH_PORT=22                       # SSH port (általában 22)
SSH_KEY=""                        # Ha üres, az alapértelmezett kulcsot használja (~/.ssh/id_ed25519)

RUNTIME="oxide"                   # "oxide" vagy "carbon"

# Szerver oldali útvonalak
OXIDE_PLUGINS_DIR="/home/rust/server/oxide/plugins"
OXIDE_LANG_DIR="/home/rust/server/oxide/lang"
CARBON_PLUGINS_DIR="/home/rust/server/carbon/plugins"
CARBON_LANG_DIR="/home/rust/server/carbon/lang"

# Konzol parancs a plugin reload-hoz (RCON vagy közvetlen server script)
# Példa Oxide: a szerver konzolja /proc/1/fd/0-ba ír
# Ha van RCON tool-od (pl. rcon-cli), cseréld le az alábbi sort
RELOAD_CMD="echo 'oxide.reload MogyAntiCheat' | tee /proc/1/fd/0 || true"

# Backup-ok helye a szerveren
BACKUP_DIR="/home/rust/backups/mogyac"

# Helyi fájlok
LOCAL_PLUGIN="MogyAntiCheat.cs"
LOCAL_LANG_EN="oxide/lang/en/MogyAntiCheat.json"
LOCAL_LANG_HU="oxide/lang/hu/MogyAntiCheat.json"

# =============================================================================
# Belső logika — ne szerkeszd
# =============================================================================

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'
CYAN='\033[0;36m'; BOLD='\033[1m'; RESET='\033[0m'

log()     { echo -e "${CYAN}[deploy]${RESET} $*"; }
ok()      { echo -e "${GREEN}[  OK  ]${RESET} $*"; }
warn()    { echo -e "${YELLOW}[ WARN ]${RESET} $*"; }
error()   { echo -e "${RED}[ERROR ]${RESET} $*" >&2; }
section() { echo -e "\n${BOLD}${CYAN}▶ $*${RESET}"; }

DRY_RUN=false
DEPLOY_PLUGIN=true
DEPLOY_LANG=true

for arg in "$@"; do
  case $arg in
    --dry-run) DRY_RUN=true ;;
    --plugin)  DEPLOY_LANG=false ;;
    --lang)    DEPLOY_PLUGIN=false ;;
    --help|-h)
      grep '^#' "$0" | head -8 | sed 's/^# \{0,\}//'
      exit 0
      ;;
    *) warn "Ismeretlen kapcsoló: $arg" ;;
  esac
done

# Útvonalak a runtime alapján
if [[ "$RUNTIME" == "carbon" ]]; then
  PLUGINS_DIR="$CARBON_PLUGINS_DIR"
  LANG_DIR="$CARBON_LANG_DIR"
  RELOAD_CMD="echo 'c.reload MogyAntiCheat' | tee /proc/1/fd/0 || true"
else
  PLUGINS_DIR="$OXIDE_PLUGINS_DIR"
  LANG_DIR="$OXIDE_LANG_DIR"
fi

# SSH kapcsolat felépítése
SSH_OPTS="-p $SSH_PORT -o ConnectTimeout=10 -o BatchMode=yes"
[[ -n "$SSH_KEY" ]] && SSH_OPTS="$SSH_OPTS -i $SSH_KEY"

ssh_run() {
  if $DRY_RUN; then
    log "[DRY] ssh $SSH_HOST: $*"
  else
    ssh $SSH_OPTS "$SSH_HOST" "$@"
  fi
}

scp_up() {
  local src="$1" dst="$2"
  if $DRY_RUN; then
    log "[DRY] scp $src → $SSH_HOST:$dst"
  else
    scp $SSH_OPTS "$src" "$SSH_HOST:$dst"
  fi
}

# =============================================================================
# 1. Előfeltételek
# =============================================================================
section "Előfeltételek ellenőrzése"

for f in "$LOCAL_PLUGIN" "$LOCAL_LANG_EN" "$LOCAL_LANG_HU"; do
  if [[ ! -f "$f" ]]; then
    error "Hiányzó fájl: $f"
    exit 1
  fi
done
ok "Helyi fájlok megvannak"

PLUGIN_VERSION=$(grep -o '"[0-9]\+\.[0-9]\+\.[0-9]\+"' "$LOCAL_PLUGIN" | head -1 | tr -d '"')
log "Plugin verzió: ${BOLD}${PLUGIN_VERSION}${RESET}"

if ! $DRY_RUN; then
  log "SSH kapcsolat tesztelése: $SSH_HOST..."
  if ! ssh $SSH_OPTS "$SSH_HOST" "echo OK" &>/dev/null; then
    error "SSH kapcsolat sikertelen: $SSH_HOST"
    error "Ellenőrizd az SSH kulcsot és a host nevet a szkript tetején"
    exit 1
  fi
  ok "SSH kapcsolat: $SSH_HOST"
fi

# =============================================================================
# 2. Biztonsági mentés
# =============================================================================
section "Biztonsági mentés"

TIMESTAMP=$(date +"%Y%m%d_%H%M%S")
BACKUP_PATH="$BACKUP_DIR/${TIMESTAMP}"

ssh_run "mkdir -p $BACKUP_PATH"
if ssh_run "[ -f $PLUGINS_DIR/MogyAntiCheat.cs ]"; then
  ssh_run "cp $PLUGINS_DIR/MogyAntiCheat.cs $BACKUP_PATH/MogyAntiCheat.cs"
  ok "Plugin mentve: $BACKUP_PATH/MogyAntiCheat.cs"
else
  warn "Nincs korábbi plugin verzió (első deploy?)"
fi

# Régi backup-ok törlése (csak az utolsó 10 marad)
ssh_run "ls -dt $BACKUP_DIR/*/ 2>/dev/null | tail -n +11 | xargs rm -rf 2>/dev/null || true"

# =============================================================================
# 3. Plugin feltöltése
# =============================================================================
if $DEPLOY_PLUGIN; then
  section "Plugin feltöltése"

  ssh_run "mkdir -p $PLUGINS_DIR"
  scp_up "$LOCAL_PLUGIN" "$PLUGINS_DIR/MogyAntiCheat.cs"
  ok "MogyAntiCheat.cs feltöltve → $PLUGINS_DIR/"
fi

# =============================================================================
# 4. Lang fájlok feltöltése
# =============================================================================
if $DEPLOY_LANG; then
  section "Lang fájlok feltöltése"

  ssh_run "mkdir -p $LANG_DIR/en $LANG_DIR/hu"
  scp_up "$LOCAL_LANG_EN" "$LANG_DIR/en/MogyAntiCheat.json"
  ok "en/MogyAntiCheat.json feltöltve"
  scp_up "$LOCAL_LANG_HU" "$LANG_DIR/hu/MogyAntiCheat.json"
  ok "hu/MogyAntiCheat.json feltöltve"
fi

# =============================================================================
# 5. Plugin reload
# =============================================================================
if $DEPLOY_PLUGIN; then
  section "Plugin újratöltése"

  if $DRY_RUN; then
    log "[DRY] Reload parancs: $RELOAD_CMD"
  else
    log "Reload parancs futtatása..."
    ssh_run "$RELOAD_CMD" || warn "Reload parancs hibát adott — ellenőrizd kézzel a szerver konzolt"
    sleep 2
    ok "Reload elküldve"
  fi
fi

# =============================================================================
# 6. Összefoglalás
# =============================================================================
section "Deploy összefoglalás"

$DRY_RUN && echo -e "${YELLOW}  ⚠  DRY RUN mód — semmi nem lett ténylegesen végrehajtva${RESET}"

echo -e "  Verzió:   ${BOLD}${PLUGIN_VERSION}${RESET}"
echo -e "  Runtime:  ${BOLD}${RUNTIME}${RESET}"
echo -e "  Szerver:  ${BOLD}${SSH_HOST}${RESET}"
echo -e "  Plugins:  ${PLUGINS_DIR}"
echo -e "  Backup:   ${BACKUP_PATH}"
$DEPLOY_PLUGIN && echo -e "  Plugin:   ${GREEN}✓ feltöltve${RESET}"
$DEPLOY_LANG   && echo -e "  Lang:     ${GREEN}✓ feltöltve (en + hu)${RESET}"

echo ""
ok "Deploy befejezve!"
