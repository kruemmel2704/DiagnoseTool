#!/bin/bash
# DiagnoseTool – Installation/Aktualisierung auf macOS ohne Gatekeeper-Warnung.
#
#   curl -fsSL https://raw.githubusercontent.com/kruemmel2704/DiagnoseTool/main/install-macos.sh | bash
#
# Lädt das neueste Release passend zur Architektur (Apple Silicon / Intel) per curl.
# Per curl geladene Dateien erhalten keine Quarantäne-Markierung, daher startet die
# ad-hoc signierte App ohne "Apple kann nicht überprüfen..."-Dialog.
set -euo pipefail

REPO="kruemmel2704/DiagnoseTool"
APP_NAME="DiagnoseTool.app"
TARGET_DIR="/Applications"

case "$(uname -m)" in
    arm64) ASSET="DiagnoseTool-macOS-AppleSilicon.dmg"; LABEL="Apple Silicon" ;;
    x86_64) ASSET="DiagnoseTool-macOS-Intel.dmg"; LABEL="Intel" ;;
    *) echo "Nicht unterstützte Architektur: $(uname -m)"; exit 1 ;;
esac

echo "==> Suche neuestes DiagnoseTool-Release für $LABEL ..."
RELEASE_JSON=$(curl -fsSL -H "Accept: application/vnd.github+json" "https://api.github.com/repos/$REPO/releases/latest")
TAG=$(printf '%s' "$RELEASE_JSON" | /usr/bin/python3 -c 'import json,sys; print(json.load(sys.stdin)["tag_name"])' 2>/dev/null \
      || printf '%s' "$RELEASE_JSON" | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -n1)
URL="https://github.com/$REPO/releases/download/$TAG/$ASSET"
echo "==> Version $TAG"

WORK=$(mktemp -d)
trap 'hdiutil detach "$WORK/mnt" -quiet >/dev/null 2>&1 || true; rm -rf "$WORK"' EXIT

echo "==> Lade $ASSET ..."
curl -fL --progress-bar -o "$WORK/$ASSET" "$URL"

echo "==> Öffne Image ..."
mkdir -p "$WORK/mnt"
hdiutil attach "$WORK/$ASSET" -nobrowse -readonly -mountpoint "$WORK/mnt" -quiet

if pgrep -x DiagnoseTool >/dev/null 2>&1; then
    echo "==> Beende laufendes DiagnoseTool ..."
    pkill -x DiagnoseTool || true
    sleep 2
fi

SUDO=""
if [ ! -w "$TARGET_DIR" ] || { [ -e "$TARGET_DIR/$APP_NAME" ] && [ ! -w "$TARGET_DIR/$APP_NAME" ]; }; then
    echo "==> Für $TARGET_DIR werden Administratorrechte benötigt."
    SUDO="sudo"
fi

echo "==> Installiere nach $TARGET_DIR/$APP_NAME ..."
$SUDO rm -rf "$TARGET_DIR/$APP_NAME"
$SUDO ditto "$WORK/mnt/$APP_NAME" "$TARGET_DIR/$APP_NAME"
$SUDO xattr -dr com.apple.quarantine "$TARGET_DIR/$APP_NAME" 2>/dev/null || true

echo "==> Fertig: DiagnoseTool $TAG ist installiert. Starte ..."
open "$TARGET_DIR/$APP_NAME"
