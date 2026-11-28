# DOD hosting

Publishes a visitor's folder (HTML/CSS/JS/PHP) at `https://<name>.ai-dod-spse.davidbenes.cz` in a second or two.
One Coolify Docker Compose resource; a new site is just a folder on a volume. Design notes: [dod-hosting-plan.md](dod-hosting-plan.md).

```
visitor PC ── deploy.sh / deploy.ps1 (zip) ──► deployer ──► /sites/<name>/
phone (QR) ──► Traefik (wildcard TLS) ──► web (Caddy) ──► php-fpm (sandboxed)
```

| Path | What |
|---|---|
| `docker-compose.yml` | `web` (Caddy), `php` (hardened php-fpm), `deployer` (.NET 10 API); volumes `sites`, `meta` |
| `web/Caddyfile` | First host label → `/sites/<label>`; PHP via FastCGI with per-site `open_basedir`; `noindex` |
| `php/` | php.ini (`disable_functions`, no URL fopen, limits), FPM pool |
| `deployer/` | `POST /deploy`, `/qr/<name>`, `/api/counter/<name>`, `/admin`, TTL cleanup |
| `deployer.Tests/` | Name + zip validation and deploy/redeploy tests (`dotnet test deployer.Tests`) |
| `scripts/` | `deploy.sh` (macOS/Linux) and `deploy.ps1` (Windows); also served at `https://deploy.…/deploy.sh` |
| `coolify/issue-wildcard-cert.sh` | Issues the wildcard certificate (manual DNS TXT) as a Traefik dynamic config |

## Coolify setup

### 1. DNS
Wildcard A record (and AAAA if the server has IPv6) at the DNS provider of davidbenes.cz:

```
*.ai-dod-spse.davidbenes.cz.   A   <Coolify server IP>
```

`deploy.ai-dod-spse…` is covered by the wildcard.

### 2. Wildcard certificate (manual, no DNS API)
A wildcard certificate needs the DNS-01 challenge. davidbenes.cz is on WEDOS and its API isn't used, so the
certificate is issued by hand on any machine with Docker and handed to Traefik as a file:

1. Run `bash coolify/issue-wildcard-cert.sh`. It prints a TXT value.
2. WEDOS > DNS for davidbenes.cz: add or update `_acme-challenge.ai-dod-spse`, type `TXT`, TTL 300, with that value,
   then click **Potvrdit změny**. The script waits until all WEDOS nameservers serve it (usually 5–15 minutes),
   then writes `~/.dod-cert/dod-wildcard.yaml` (certificate + private key inline; keep it private) and copies it
   to the clipboard.
3. Coolify > Servers > server > Proxy > Dynamic Configurations: add (or replace) `dod-wildcard.yaml` with that content.
   Traefik reloads dynamic configs by itself; no proxy restart.
4. Check: `curl -vI https://deploy.ai-dod-spse.davidbenes.cz 2>&1 | grep -E "subject|expire"` shows
   `*.ai-dod-spse.davidbenes.cz`.

**The certificate lasts 90 days and does not renew itself.** The current one expires **2027-02-26**; repeat these
steps before then (each run gives a new TXT value). Traefik serves this certificate for every matching host, so
Coolify doesn't request per-site certificates.

**Fallback without a DNS token:** give the `web` service fixed domains `https://stul1.ai-dod-spse.davidbenes.cz`, …, `stul20`
in Coolify (each gets a normal HTTP-challenge certificate), skip the dynamic config, and have visitors deploy to those names.

### 3. The resource
1. New resource > Docker Compose, from this repository (or paste `docker-compose.yml` and use a Git source for the build contexts).
2. Environment variables:

   | Variable | Value |
   |---|---|
   | `EVENT_TOKEN` | Shared token written on the whiteboard for the day (≥ 8 chars) |
   | `ADMIN_PASSWORD` | Password for `/admin` (≥ 8 chars, any username) |
   | `SITE_DOMAIN` | `ai-dod-spse.davidbenes.cz` (default) |
   | `SITE_TTL_DAYS` | Days after last deploy before a site is deleted (default 14) |

3. Domains: add only one, in Domains > Add domain: service `deployer`, `deploy.ai-dod-spse.davidbenes.cz`,
   port `8080` (confirm "Use This Port Anyway"). Delete the `www.deploy…` domain Coolify adds automatically; it has
   no DNS record.

   Leave `web` without a domain: Coolify rejects wildcard domains, so the visitor sites are routed by the Traefik
   labels on `web` in [docker-compose.yml](docker-compose.yml) (priority 1, so `deploy.` still goes to the deployer).
   Those labels name the proxy network after the resource UUID (`1wqyvgpdb6mqamma8biv75hs`); if the resource is
   recreated, update `traefik.docker.network` to the new UUID.
4. Deploy, then open `https://deploy.ai-dod-spse.davidbenes.cz/`.

## Using it at the workshop

macOS / Linux:
```bash
curl -fsSLO https://deploy.ai-dod-spse.davidbenes.cz/deploy.sh
DOD_TOKEN=<token> bash deploy.sh ./muj-web jiri
```

Windows:
```powershell
curl.exe -fsSLO https://deploy.ai-dod-spse.davidbenes.cz/deploy.ps1
powershell -ExecutionPolicy Bypass -File deploy.ps1 .\muj-web jiri
```

- Instead of `DOD_TOKEN=…` on every run, put it in a `.env` file in the website folder (or the folder you run the
  script from). The script reads `DOD_TOKEN` and `DOD_SERVER` from it, never executes it, and never uploads it:
  ```
  DOD_TOKEN=<token>
  ```
  Order: environment variable, then `<folder>/.env`, then `./.env`; if there's still no token, the script asks.
- The name is normalized (`Jiří Novák` → `jiri-novak`), 3–30 characters, `deploy/www/admin/api/…` are reserved.
- First deploy saves `.deploy-secret` and `.deploy-name` in the folder; redeploys from that folder just work
  (`deploy.sh ./muj-web` without a name). Another folder can't take over an existing name.
- The script prints the URL and a terminal QR. For the monitor open `https://deploy.…/qr/<name>`.
- Static visit counter for W1 (no PHP needed):
  ```js
  fetch("https://deploy.ai-dod-spse.davidbenes.cz/api/counter/jiri").then(r => r.json()).then(d => el.textContent = d.count);
  ```
- PHP sites can write only to `data/` in their own folder (`$_SERVER['SITE_DATA_DIR']`), e.g. a counter file or SQLite.
- Admin: `https://deploy.…/admin` lists sites (age, size, visits, QR), deletes one or all.

## Limits and safety
- Only someone with the event token (`EVENT_TOKEN`) can publish. Change it in Coolify after the event (or each day) and
  redeploy to lock everyone out. Each site also has its own secret, so a token holder can't overwrite someone else's site.
- Zip ≤ 10 MB, ≤ 500 files, ≤ 40 MB unpacked, extension allowlist, no `..`/absolute paths/symlinks, `index.html` or `index.php` at the root
  (a single top-level folder is unwrapped). Dotfiles and OS junk are dropped.
- Redeploys swap the folder atomically; a failed upload leaves the old version online. `data/` is kept.
- PHP: per-site `open_basedir` (set by Caddy via `PHP_VALUE`), `.user.ini` disabled, no `exec`/`system`/`mail`/sockets/`ini_set`,
  5 s / 64 MB per request, read-only container, 1 CPU / 512 MB, no outbound network (`internal` Docker network), all capabilities dropped.
  Site files are root-owned; PHP can write only to `data/`.
- All responses send `X-Robots-Tag: noindex`. Sites are deleted `SITE_TTL_DAYS` after their last deploy.
- Workshop rule (not enforced): no photos or surnames on pages.

## Local development
```bash
cp docker-compose.override.yml.example docker-compose.override.yml
printf 'EVENT_TOKEN=devtoken123\nADMIN_PASSWORD=devadmin123\n' > .env
docker compose up --build
DOD_SERVER=http://localhost:8081 DOD_TOKEN=devtoken123 bash scripts/deploy.sh ./sample jiri
curl http://jiri.dod.localhost:8080/
```
Browsers resolve `*.localhost` to 127.0.0.1, so `http://jiri.dod.localhost:8080` works directly.
