# DOD hosting service: plan

Service that accepts a folder of files (HTML, CSS, JS, optionally PHP) and publishes it at `https://[name].ai-dod-spse.davidbenes.cz`. Runs in Coolify. Used repeatedly across open-day workshops (Krok 0 and the "Nasaď stránku a zobraz QR kód" step).

## Core decision

Do not create a Coolify resource per visitor. Deploy **one** Coolify app that answers for every `*.ai-dod-spse.davidbenes.cz` host and maps the subdomain to a folder on a volume.

A new site is then just "unzip into `/sites/[name]`": no Coolify API call, no new certificate, no redeploy. Publishing takes a second or two.

## Architecture

One Docker Compose resource in Coolify: two containers, one shared volume.

| Part | Role |
|---|---|
| `web` (Caddy + PHP-FPM, or FrankenPHP) | Serves `/sites/{subdomain}` for any host; static files directly, `.php` through PHP |
| `deployer` (small API, e.g. ASP.NET minimal API) | `POST /deploy` takes a zip and a name, validates, extracts, returns the URL and QR |
| volume `/sites` | One folder per visitor; read-only in `web`, read-write in `deployer` |

Routing in `web` is a single rule. Coolify's proxy terminates TLS, so Caddy listens on plain HTTP:

```
http://*.ai-dod-spse.davidbenes.cz {
    root * /sites/{labels.3}
    php_fastcgi php:9000
    file_server
}
```

The deployer lives on a fixed host: `deploy.ai-dod-spse.davidbenes.cz`.

```
visitor PC ── deploy.sh (zip) ──► deployer ──► /sites/[name]/
                                                    ▲
phone (QR) ──► Traefik (wildcard TLS) ──► web ──────┘
```

## DNS and TLS (hard prerequisite)

- **DNS:** wildcard A record `*.ai-dod-spse.davidbenes.cz` pointing at the Coolify server.
- **Certificate:** one wildcard certificate. Traefik can only get wildcard certificates through the DNS challenge; Coolify's default is the HTTP challenge. This needs an API token for the DNS provider of davidbenes.cz, ideally restricted to that zone.
- **Setup:** switch Traefik to the DNS challenge, then request the wildcard certificate in a dynamic configuration file (Servers > server > Proxy > Dynamic Configurations).
- **Why not per-site certificates:** Let's Encrypt limits certificates per registered domain per week; a busy open day could hit it, and each new site would wait for issuance.
- **Fallback without a DNS token:** pre-create a fixed set of names (e.g. `stul1` to `stul20`) with normal certificates.

Coolify docs:
- https://coolify.io/docs/knowledge-base/proxy/traefik/dns-challenge
- https://coolify.io/docs/knowledge-base/proxy/traefik/wildcard-certs

## Deploy flow

1. `deploy.sh` (plus `deploy.ps1` if the school PCs run Windows) zips the visitor's folder.
2. It sends the zip with `name` and a shared event token to `POST /deploy`.
3. The deployer validates and extracts to `/sites/[name]`, replacing the previous version.
4. The script prints the URL and a QR code in the terminal. The deployer also serves `/qr/[name]` as a full-screen page for the monitor.

On first deploy the API returns a per-site secret that the script saves in the folder (`.deploy-secret`). Redeploys must send it, so another visitor cannot overwrite an existing name.

### API sketch

| Endpoint | Purpose |
|---|---|
| `POST /deploy` | multipart: `name`, `zip`, header `X-Event-Token`, optional `X-Site-Secret` |
| `GET /qr/[name]` | Full-screen QR page |
| `GET /api/counter/[name]` | Increments and returns a visit count (keeps W1 static) |
| `GET /admin` | Password-protected list of sites, delete one, delete all |

## Validation in the deployer

- **Name:** `[a-z0-9-]`, 3 to 30 characters, diacritics stripped (Jiří becomes `jiri`), reserved list (`deploy`, `www`, `admin`, `api`).
- **Zip:** size cap (about 10 MB), file count cap, extension allowlist, no symlinks, no `..` paths.
- **Required:** `index.html` or `index.php` at the root.

## PHP

Static files are harmless. PHP means running visitor-generated code on the server. If PHP is enabled:

- `open_basedir` per site, so one site cannot read another.
- `disable_functions` for `exec`, `shell_exec`, `system`, `proc_open`, `popen`, `mail` and similar.
- Container with a read-only root filesystem, CPU and memory limits, no outbound network.
- One small writable `data/` folder per site (enough for a counter in a file or SQLite).

Cheaper alternative: on the W1 sheet only the visit counter needs a server. With `GET /api/counter/[name]` in the deployer, W1 stays pure static, and PHP is enabled per site only for workshops that need it.

## Cleanup and privacy

- Each site stores its creation time; a daily job deletes sites older than N days.
- Admin page: list, delete one, delete all.
- All sites send `X-Robots-Tag: noindex`.
- No photos or surnames on pages (workshop rule; the service cannot enforce it).

## Build order

1. Wildcard DNS and wildcard certificate; test with a hand-made folder.
2. `web` container with host-to-folder routing.
3. Deployer API, `deploy.sh`, QR output.
4. TTL cleanup and admin page.
5. PHP hardening, if PHP stays.

## Open questions

1. Which DNS provider hosts davidbenes.cz, and can an API token be issued?
2. Is PHP needed on day one, or only for later workshops?
3. How many sites per day, and how long should they live afterwards?
4. Do the workshop PCs run Windows, macOS or Linux (decides the deploy script)?
