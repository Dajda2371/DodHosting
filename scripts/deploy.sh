#!/usr/bin/env bash
# Publishes a folder to https://<name>.ai-dod-spse.davidbenes.cz
# Usage: bash deploy.sh [folder] [name]   or   bash deploy.sh [name]
#   folder defaults to the folder this script is in, so you can copy deploy.sh into the website and run it.
#   DOD_TOKEN   event token
#   DOD_SERVER  deployer URL (default https://deploy.ai-dod-spse.davidbenes.cz)
# Each is taken from the environment, else from .env in the website folder, else from .env in the
# current folder; a missing token is asked for. .env is never uploaded (dotfiles are skipped).
set -euo pipefail

# Reads KEY from a .env file without executing it: KEY=value, export KEY=value, quotes optional.
env_value() {
  [[ -f "$2" ]] || return 0
  sed -n "s/^[[:space:]]*\(export[[:space:]][[:space:]]*\)\{0,1\}$1[[:space:]]*=[[:space:]]*//p" "$2" \
    | tail -1 | tr -d '\r' | sed -e 's/[[:space:]]*$//' -e 's/^"\(.*\)"$/\1/' -e "s/^'\(.*\)'$/\1/"
}
setting() { # environment > <folder>/.env > ./.env
  local v="${!1:-}"
  [[ -z "$v" ]] && v="$(env_value "$1" "$DIR/.env")"
  [[ -z "$v" && "$(cd "$DIR" && pwd)" != "$PWD" ]] && v="$(env_value "$1" ./.env)"
  printf '%s' "$v"
}

# Without a folder argument, publish the folder this script is in (copy deploy.sh into the website).
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT_NAME="$(basename "${BASH_SOURCE[0]}")"
DIR="${1:-$SCRIPT_DIR}"
NAME="${2:-}"
# A single argument that isn't a folder is the site name: `bash deploy.sh jiri`.
if [[ $# -eq 1 && ! -d "$1" ]]; then DIR="$SCRIPT_DIR"; NAME="$1"; fi

if [[ ! -d "$DIR" ]]; then echo "Složka '$DIR' neexistuje." >&2; exit 1; fi
DIR="$(cd "$DIR" && pwd)"
# Exact-case check: the server runs on Linux, so Index.html is not index.html there.
if ! ls "$DIR" | grep -qxE 'index\.(html|htm|php)'; then
  echo "Ve složce '$DIR' chybí index.html (nebo index.php)." >&2
  others="$(cd "$DIR" && ls -1 | grep -iE '\.(html?|php)$' | head -5 | tr '\n' ' ' || true)"
  [[ -n "$others" ]] && echo "  Nalezené stránky: $others(hlavní stránka se musí jmenovat přesně index.html)" >&2
  subs="$(cd "$DIR" && for f in */index.html */index.htm */index.php; do [[ -f "$f" ]] && printf '%s ' "${f%/*}"; done; true)"
  [[ -n "$subs" ]] && echo "  index je v podsložce: $subs- přesuň deploy.sh do ní, nebo spusť: bash $SCRIPT_NAME <složka>" >&2
  exit 1
fi
SERVER="$(setting DOD_SERVER)"; SERVER="${SERVER:-https://deploy.ai-dod-spse.davidbenes.cz}"
SECRET_FILE="$DIR/.deploy-secret"
SAVED_NAME_FILE="$DIR/.deploy-name"
if [[ -z "$NAME" && -f "$SAVED_NAME_FILE" ]]; then NAME="$(cat "$SAVED_NAME_FILE")"; fi
if [[ -z "$NAME" ]]; then read -rp "Jméno webu (např. jiri): " NAME; fi
TOKEN="$(setting DOD_TOKEN)"
if [[ -z "$TOKEN" ]]; then read -rsp "Token akce: " TOKEN; echo; fi

command -v zip >/dev/null || { echo "Chybí příkaz 'zip'." >&2; exit 1; }
command -v curl >/dev/null || { echo "Chybí příkaz 'curl'." >&2; exit 1; }

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
# Dotfiles (.env, .deploy-secret) and the deploy scripts themselves are never uploaded.
(cd "$DIR" && zip -qr "$TMP/site.zip" . -x '.*' -x '*/.*' -x '__MACOSX/*' \
  -x "$SCRIPT_NAME" -x 'deploy.sh' -x 'deploy.ps1')

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
