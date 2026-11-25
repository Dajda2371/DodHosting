#!/usr/bin/env bash
# Publishes a folder to https://<name>.ai-dod-spse.davidbenes.cz
# Usage: bash deploy.sh <folder> <name>
#   DOD_TOKEN   event token (asked for if not set)
#   DOD_SERVER  deployer URL (default https://deploy.ai-dod-spse.davidbenes.cz)
set -euo pipefail

SERVER="${DOD_SERVER:-https://deploy.ai-dod-spse.davidbenes.cz}"
DIR="${1:-}"
NAME="${2:-}"

if [[ -z "$DIR" ]]; then read -rp "Složka s webem: " DIR; fi
if [[ ! -d "$DIR" ]]; then echo "Složka '$DIR' neexistuje." >&2; exit 1; fi
if [[ ! -f "$DIR/index.html" && ! -f "$DIR/index.php" ]]; then
  echo "Ve složce '$DIR' chybí index.html (nebo index.php)." >&2; exit 1
fi
SECRET_FILE="$DIR/.deploy-secret"
SAVED_NAME_FILE="$DIR/.deploy-name"
if [[ -z "$NAME" && -f "$SAVED_NAME_FILE" ]]; then NAME="$(cat "$SAVED_NAME_FILE")"; fi
if [[ -z "$NAME" ]]; then read -rp "Jméno webu (např. jiri): " NAME; fi
TOKEN="${DOD_TOKEN:-}"
if [[ -z "$TOKEN" ]]; then read -rsp "Token akce: " TOKEN; echo; fi

command -v zip >/dev/null || { echo "Chybí příkaz 'zip'." >&2; exit 1; }
command -v curl >/dev/null || { echo "Chybí příkaz 'curl'." >&2; exit 1; }

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
(cd "$DIR" && zip -qr "$TMP/site.zip" . -x '.*' -x '*/.*' -x '__MACOSX/*')

SECRET_HEADER=()
if [[ -f "$SECRET_FILE" ]]; then SECRET_HEADER=(-H "X-Site-Secret: $(cat "$SECRET_FILE")"); fi

STATUS=$(curl -sS -o "$TMP/out.json" -w '%{http_code}' \
  -H "X-Event-Token: $TOKEN" ${SECRET_HEADER[@]+"${SECRET_HEADER[@]}"} \
  -F "name=$NAME" -F "zip=@$TMP/site.zip;type=application/zip" \
  "$SERVER/deploy") || { echo "Server není dostupný: $SERVER" >&2; exit 1; }

json() { # tiny JSON string reader (no jq/python needed); our values never contain quotes
  sed -n "s/.*\"$1\":\"\([^\"]*\)\".*/\1/p" "$TMP/out.json" | head -1
}

if [[ "$STATUS" != 200 ]]; then
  echo "Chyba ($STATUS): $(json error)" >&2; exit 1
fi

URL="$(json url)"
REAL_NAME="$(json name)"
NEW_SECRET="$(json secret)"
if [[ -n "$NEW_SECRET" ]]; then printf '%s' "$NEW_SECRET" > "$SECRET_FILE"; fi
printf '%s' "$REAL_NAME" > "$SAVED_NAME_FILE"

echo
curl -fsS "$SERVER/qr/$REAL_NAME/text" 2>/dev/null || true
echo
echo "  Hotovo! Web běží na:  $URL"
echo "  QR na celou obrazovku: $(json qrUrl)"
echo
