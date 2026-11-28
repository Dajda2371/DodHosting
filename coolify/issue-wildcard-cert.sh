#!/usr/bin/env bash
# Issues (or renews) the wildcard certificate *.ai-dod-spse.davidbenes.cz with Let's Encrypt using a
# manual DNS-01 challenge: no DNS API needed, you add one TXT record by hand in the WEDOS web UI.
# Needs Docker. Output: a Traefik dynamic config with the certificate and key inline, to paste into
# Coolify > Servers > <server> > Proxy > Dynamic Configurations (replacing the previous one).
#
# Usage: bash coolify/issue-wildcard-cert.sh [work-dir]   (default ~/.dod-cert, keep it private)
set -euo pipefail

DOMAIN="ai-dod-spse.davidbenes.cz"
WORK="${1:-$HOME/.dod-cert}"
mkdir -p "$WORK/data" "$WORK/hooks" "$WORK/out"
chmod 700 "$WORK"
rm -f "$WORK/out/txt-value" "$WORK/out/status"

cat > "$WORK/hooks/auth.sh" <<'HOOK'
#!/bin/sh
# Publishes the value for the human, then waits until all WEDOS nameservers serve it.
echo "$CERTBOT_VALIDATION" > /out/txt-value
NAME="_acme-challenge.${CERTBOT_DOMAIN}"
i=0
while [ $i -lt 720 ]; do   # up to 2 hours
  ok=1
  for ns in ns.wedos.cz ns.wedos.eu ns.wedos.com ns.wedos.net; do
    nslookup -type=TXT "$NAME" "$ns" 2>/dev/null | grep -q "$CERTBOT_VALIDATION" || ok=0
  done
  if [ $ok = 1 ]; then echo visible > /out/status; sleep 20; exit 0; fi
  i=$((i+1)); sleep 10
done
echo timeout > /out/status; exit 1
HOOK
chmod +x "$WORK/hooks/auth.sh"

docker run --rm -v "$WORK/data:/etc/letsencrypt" -v "$WORK/hooks:/hooks:ro" -v "$WORK/out:/out" \
  certbot/certbot:v3.0.1 certonly --manual --preferred-challenges dns \
  --manual-auth-hook /hooks/auth.sh --agree-tos --register-unsafely-without-email --non-interactive \
  --key-type ecdsa --force-renewal --cert-name "$DOMAIN" -d "*.$DOMAIN" > "$WORK/out/certbot.log" 2>&1 &
CERTBOT=$!

until [[ -s "$WORK/out/txt-value" ]] || ! kill -0 $CERTBOT 2>/dev/null; do sleep 2; done
if [[ -s "$WORK/out/txt-value" ]]; then
  cat <<MSG

In WEDOS > DNS for davidbenes.cz, add (or update) this record, then click "Potvrdit změny":

  Name: _acme-challenge.ai-dod-spse    Type: TXT    TTL: 300
  Data: $(cat "$WORK/out/txt-value")

Waiting until it is visible on all WEDOS nameservers (usually 5-15 minutes)...
MSG
fi
wait $CERTBOT || { echo "certbot failed:" >&2; tail -20 "$WORK/out/certbot.log" >&2; exit 1; }

LIVE="$WORK/data/live/$DOMAIN"
OUT="$WORK/dod-wildcard.yaml"
EXPIRES="$(openssl x509 -in "$LIVE/fullchain.pem" -noout -enddate | cut -d= -f2)"
( umask 077
  { echo "# Wildcard certificate for *.$DOMAIN (Let's Encrypt, manual DNS-01). Expires: $EXPIRES"
    echo "tls:"
    echo "  certificates:"
    echo "    - certFile: |"; sed 's/^/        /' "$LIVE/fullchain.pem"
    echo "      keyFile: |"; sed 's/^/        /' "$LIVE/privkey.pem"
  } > "$OUT" )
COPIED=""
if command -v pbcopy >/dev/null; then pbcopy < "$OUT"; COPIED=" (also copied to the clipboard)"; fi
echo
echo "Done. Expires: $EXPIRES"
echo "Paste $OUT$COPIED into Coolify > Proxy > Dynamic Configurations as 'dod-wildcard.yaml'."
