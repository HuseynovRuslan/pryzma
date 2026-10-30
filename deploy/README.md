# PRIZMA — production (prizma.qrlog.az)

Written for the person operating the server. Everything here is an instruction, not a description of the code;
the code is documented in the repository's main README.

**Domain:** https://prizma.qrlog.az (HTTP redirects to HTTPS at the edge).
**Host:** the shared QRLog VPS (94.20.153.137, Ubuntu 24.04, Docker). Other production systems run on the same
machine; nothing here touches them. Reach it as `deploy` with the `qrlog_vps_l` key.

No secret value appears in this file. Secrets live only in `/opt/meydan/.env` (owner `deploy`, mode 600).

## Layout on the server

| Path | What |
|---|---|
| `/opt/meydan/.env` | every secret and environment-specific value; the only copy — back it up separately |
| `/opt/meydan/releases/<stamp>/` | unpacked releases (the last five are kept) |
| `/opt/meydan/current` → | symlink to the release that is running; every script below reads from it |
| `/var/lib/meydan/uploads/` | participants' originals, public derivatives, covers — owned by UID 1654 (the API container's non-root user), mode 750; **never served as static files** |
| `/var/lib/meydan/backups/` | nightly dumps + archives (root only, 30-day retention) and the `backup.log` / `restore-test.log` |
| `/etc/cron.d/meydan` | the two scheduled jobs (copied from `deploy/cron.meydan`) |
| Docker volume `meydan_pgdata` | PostgreSQL data |

Compose project `meydan` (`deploy/compose.yml`), four services:

| Service | Container | Image | Network(s) | Notes |
|---|---|---|---|---|
| `db` | `meydan-db-1` | postgres:18-alpine | internal | no published port; superuser `postgres` used only by init and maintenance |
| `migrate` | one-shot | meydan-api:`<release>` | internal | `dotnet Meydan.Api.dll migrate` — explicit step, exits |
| `api` | `meydan-api-1` | meydan-api:`<release>` | internal (alias `meydan-api`) | runs as UID 1654, no published port, health check `/health/live` |
| `web` | `meydan-web-1` | meydan-web:`<release>` | internal + `attendanceqr_default` (alias `meydan-web`) | nginx: SPA + `/api` + `/health` proxy; loopback port 8040 for diagnostics |

**Database:** `meydan`, application role `meydan` (LOGIN only — not a superuser, cannot create roles or databases).
Created once by `deploy/db/init/01-init-app-role-and-db.sh` when the volume is first initialised.

**Edge:** the QRLog stack's Caddy (`attendanceqr-caddy-1`) terminates TLS (Let's Encrypt, auto-renewed) and proxies
`prizma.qrlog.az` to `meydan-web:80`, and answers `meydan.qrlog.az` with a 308 to the same path on
`prizma.qrlog.az` (old links, QR codes and shared URLs keep working). Its site block lives in the
**AttendanceQR repository's `Caddyfile`** — a block
typed only on the server is erased by QRLog's next deploy. HSTS is set there; every other security header comes from
MEYDAN itself (`frontend/nginx-headers.conf` for the SPA, the API's own middleware for `/api`).

## Deploying a release

On a workstation, from the repository (any state — the release id records the commit and whether the tree was clean):

```bash
deploy/scripts/release.sh                    # → design-review/releases/meydan-<id>.tgz
scp design-review/releases/meydan-<id>.tgz deploy@94.20.153.137:/tmp/
```

On the server:

```bash
/opt/meydan/current/deploy/scripts/deploy.sh /tmp/meydan-<id>.tgz
```

`deploy.sh` does, in this order: unpack → **safety backup** (skipped only when no database exists yet) → build the
two images tagged with the release id → start `db` if it is not running (**never recreated** by a deploy) →
**migrate** (`docker compose run --rm --no-deps migrate`; the API refuses to start on a database with pending
migrations, so this can never be forgotten) → start `api` + `web` → wait for `/health/ready` → move `current`. Expect a
few seconds without service while `api`/`web` are recreated; the QRLog stack is not touched.

**Traceability.** A release built from a clean checkout is named by its commit (`meydan-<short hash>.tgz`, image tag
`<short hash>`); a dirty tree gets `-wip-<time>` and must not stay in production. Both images carry the full commit:

```bash
docker inspect meydan-api-1 --format '{{index .Config.Labels "org.opencontainers.image.revision"}}'
cat /opt/meydan/current/RELEASE
```

**First admin.** The first `migrate` run created the admin from `Seed__AdminEmail` / `Seed__AdminPassword` (values the
`migrate` container alone receives). Once the admin could sign in, **both lines were removed from `.env`** — the
password is not stored on the server; the operator keeps it off-server. A `migrate` run without them logs "No bootstrap
admin configured" and never creates or changes an account. MEYDAN has no in-session "change password" and password
reset needs e-mail (see "Turning e-mail on"), so keep that off-server copy until SMTP exists.

### Everyday commands

```bash
cd /opt/meydan
alias mc='docker compose --env-file /opt/meydan/.env -f /opt/meydan/current/deploy/compose.yml'
mc ps                                    # status
mc logs -f --tail 200 api                # JSON logs, one line per event (never secrets, tokens or file contents)
mc restart api                           # restart the API (e.g. after editing .env: recreate instead — see below)
mc up -d --no-deps api                   # apply a changed .env to the API (recreates the container)
mc run --rm migrate                      # apply migrations by hand (idempotent)
curl -s -H 'Host: prizma.qrlog.az' http://127.0.0.1:8040/health/ready   # {"status":"Healthy",...}; the Host header matters (AllowedHosts)
curl -sI https://prizma.qrlog.az/ | head -5   # through the edge
```

### Rollback

Application rollback = deploying the previous release again:

```bash
ls -1t /opt/meydan/releases/                    # earlier releases are still unpacked, images still tagged
/opt/meydan/current/deploy/scripts/deploy.sh <the previous tarball>   # or: re-run release.sh at the old commit
```

**Rollback never downgrades the database.** EF Core migrations are forward-only. Old code runs fine on a schema that
only *added* things (the usual case). If the release you are backing out of *renamed or dropped* something, stop:
restore the safety dump `deploy.sh` took before it migrated (see "Restore") into a fresh database and point
`ConnectionStrings__Meydan` at it — do not start old code on the new schema and hope.

## Backup and restore

`/etc/cron.d/meydan` runs, as root, in the server's time zone (Asia/Baku):

- **03:20 nightly** — `deploy/scripts/backup.sh`: `pg_dump` (custom format, taken inside the `db` container over the
  unix socket, so no password is on any command line) + a `tar.gz` of `/var/lib/meydan/uploads` + a `.sha256` file,
  into `/var/lib/meydan/backups`. Both artefacts are re-read before they count. Retention **30 days** (a conservative
  default; nobody has set a rule yet). Same disk only — copy the directory off the machine for a real disaster plan.
- **04:40 Sundays** — `deploy/scripts/restore-test.sh`: restores the newest dump into a throwaway database
  `meydan_restore_test_<pid>`, counts users/submissions/migrations, checks the archive and the checksums, drops the
  throwaway. Production data is never touched.

Run either by hand with `sudo bash /opt/meydan/current/deploy/scripts/backup.sh` (or `restore-test.sh`).

**What these backups do and do not protect against.** They live on the same disk as the database:

| Protected (the backup itself survives) | **Not** protected |
|---|---|
| a bad deployment or migration (`deploy.sh` also dumps right before it migrates) | disk failure |
| accidental deletion or damage of data inside the application | loss of the server or of the hosting account |
| logical corruption noticed within the 30-day retention | ransomware or anyone with root on this host |

An off-server copy of `/var/lib/meydan/backups` (plus `/opt/meydan/.env`, kept separately) is the recommended next
step after launch. It needs a destination and credentials nobody has provided yet, so none is configured.

**Restore for real** (a new, empty database — never over the live one while the API runs):

```bash
mc stop api web
mc exec -T db createdb -U postgres meydan_restored
mc exec -T db pg_restore -U postgres --dbname meydan_restored --no-owner --no-privileges < /var/lib/meydan/backups/meydan-db-<stamp>.dump
mc exec -T db psql -U postgres -c 'ALTER DATABASE meydan_restored OWNER TO meydan'
# files: extract into a NEW directory, then swap it in (keep the old one until the site has been checked)
sudo mkdir /var/lib/meydan/uploads-restored && sudo tar -xzf /var/lib/meydan/backups/meydan-files-<stamp>.tar.gz -C /var/lib/meydan/uploads-restored
sudo chown -R 1654:1654 /var/lib/meydan/uploads-restored
# then: APP_DB_NAME=meydan_restored in .env (or rename databases), swap the uploads directories, `mc up -d api web`, check /health/ready
```

`.env` is not part of any backup: keep a copy of it in the same place you keep other server secrets.

## Configuration reference (names only)

All read from `/opt/meydan/.env` by `deploy/compose.yml`; a variable not listed in the compose file reaches nothing.

| Variable | Meaning |
|---|---|
| `POSTGRES_PASSWORD` | PostgreSQL superuser (bootstrap + maintenance only) |
| `APP_DB_NAME`, `APP_DB_USER`, `APP_DB_PASSWORD` | the application's database and least-privilege role |
| `SHARED_PROXY_NETWORK` | `attendanceqr_default` — the edge proxy's network |
| `APP_HTTP_PORT` | loopback diagnostics port (8040) |
| `IMAGE_TAG` | written by `deploy.sh`: the running release |
| `Jwt__SigningKey` | access-token key, generated for production (never the development key) |
| `App__PublicUrl` | `https://prizma.qrlog.az` — links in e-mails and the QRLog return address are built from it |
| `AllowedHosts` | `prizma.qrlog.az;localhost;meydan-api` (localhost for the container health check) |
| `ReverseProxy__KnownNetworks__0` | `172.16.0.0/12`: `X-Forwarded-*` is trusted only from this stack's own nginx |
| `Email__Mode` | `Disabled` today; `Smtp` once a provider exists |
| `Email__FromAddress`, `Email__FromName`, `Email__Smtp__Host`, `Email__Smtp__Port`, `Email__Smtp__Security`, `Email__Smtp__Username`, `Email__Smtp__Password` | the provider, when `Email__Mode=Smtp` |
| `QrlogSso__Enabled` | `true` once QRLog's side is deployed (see below) |
| `QrlogSso__VouchSecret` | shared with QRLog (`ExternalSignIn__Apps__meydan__VouchSecret` there); ≥ 32 characters |
| `QrlogSso__AuthorizeUrl` | `https://app.qrlog.az/signin/meydan?code={code}&return={return}` |
| `Seed__AdminEmail`, `Seed__AdminPassword` | first admin; read by `migrate` only |

Changing a value: edit `.env`, then `mc up -d --no-deps api` (the API reads its environment at start).

### Data Protection keys (password-reset tokens)

ASP.NET Data Protection keys live in the `DataProtectionKeys` table of the MEYDAN database, so every API instance and
every restore uses the same key ring. They are **not** encrypted at the application-key level — the API logs "No XML
encryptor configured" once at start; that is expected, not an error. What protects them today is where they are: a
database with no published port, reachable only from this stack's private network, a least-privilege role, and
root-only backups. Encrypting the key ring (a certificate or a key-management service via `ProtectKeysWith…`) is a
possible later hardening step and should be planned, not added during a deployment.

### Turning e-mail on

**Today: `Email__Mode=Disabled`, on purpose, until real SMTP credentials exist.** Password recovery by e-mail therefore
does not work yet, and the forgot-password page says so before anyone types an address.

Set `Email__Mode=Smtp` and the `Email__Smtp__*` / `Email__FromAddress` values, then `mc up -d --no-deps api`. Nothing
else changes: password-reset and submission-receipt mails already queue in the database (`EmailOutbox`) and the
worker starts delivering. Until then the site says, before anyone types an address, that password reset by e-mail is
not active yet; account enumeration is not affected (the form is simply not offered).

### "QRLog ilə daxil ol"

QRLog is not an OpenID provider; the two sites share an HMAC secret and QRLog's server vouches for a signed-in
employee (the Kitabxana pattern). Turning it off at any time: `QrlogSso__Enabled=false` in `.env`, then
`mc up -d --no-deps api` — the button disappears and every endpoint answers "not active". The steps that switched it on:

1. Deploy the QRLog change (`ExternalSignInController` + the `/signin/:app` approval page) from the AttendanceQR
   repository with `ops/deploy-prod.sh`, outside the scan peaks (07:30–09:30, 17:00–19:00).
2. In `/opt/attendanceqr/.env` set `ExternalSignIn__Apps__meydan__VouchSecret` (same value as MEYDAN's
   `QrlogSso__VouchSecret`), `ExternalSignIn__Apps__meydan__ConfirmUrl=https://prizma.qrlog.az/api/auth/qrlog/confirm`
   and `ExternalSignIn__Apps__meydan__AnyTenant=true` — the compose file already names them.
3. In `/opt/meydan/.env` set `QrlogSso__Enabled=true`, then `mc up -d --no-deps api`.

How it works, for the record: MEYDAN opens a short-lived ticket (a random code in the address, a claim secret in an
HttpOnly cookie), sends the person to QRLog, they approve with one tap on their signed-in phone, and **QRLog's server**
posts `{code, subject = employee id, name, e-mail (unverified)}` to MEYDAN signed with HMAC-SHA256 over the exact body
plus a timestamp. MEYDAN checks the signature in constant time, the timestamp (±5 min), single use of the code, and
then signs the browser in — or, when the e-mail already belongs to an unlinked MEYDAN account, refuses and asks the
person to sign in with the password and link QRLog from the profile. A QRLog identity never grants the Admin role —
and it never opens an admin session at all: linking QRLog to an administrator account is refused, and a QRLog login
that maps to an account holding the Admin role is refused too. Administrators sign in with e-mail and password only.

## Health, logs, monitoring

- `/health/live` — the process answers. `/health/ready` — PostgreSQL reachable + no pending migrations + storage
  writable. Both public, both status-only. Docker's own health check uses `/health/live`.
- Logs: `mc logs api` (JSON, correlation id per request; never passwords, tokens, reset links, connection strings,
  answers or file contents), `mc logs web` (nginx access log), `docker logs attendanceqr-caddy-1` for TLS/edge.
- Nothing pages anyone yet. The QRLog stack's watchdog watches only QRLog. A one-line check that would do:
  `curl -fsS https://prizma.qrlog.az/health/ready`.

## Test data left in production

The launch smoke tests created participant accounts `sinaq-…@prizma.qrlog.az` and submissions `SUB-2026-00001` –
`SUB-2026-00003`, all **rejected** with an internal note ("deploy smoke testi"), so none of them can ever be published.
MEYDAN has no supported way to delete an account or a submitted work, and deleting them with SQL would break the
reference counter and the audit trail — so they stay. Real submissions therefore start at `SUB-2026-00004`.

## Demo data (`seed-demo` / `remove-demo`)

Illustrative content from `./demo-assets` (mapped by its `manifest.json`), written into the real database through the
real pipelines and marked `IsDemo`: 3 projects, 3 competitions (open / upcoming / finished), 15 participants, entries
for the Bakı chevron competition (one public entry per supplied chevron, plus drafts and rejected ones) and winners for
8 settlements, the Bakı results banner. Participants have `@example.invalid` addresses, no password, are locked and
are never linked to QRLog. Admin lists mark every demo row "Nümunə" and filter it with "Məlumat".

It never publishes or unpublishes a result, never replaces an existing winner or cover, and adds **no winners and no
banner to a result that is already published** (they would be announced at once) — to include them: unpublish,
run `seed-demo` again, review, publish. Both commands are idempotent. Demo entries take reference numbers from the
real counter; after `remove-demo` those numbers are simply unused.

```bash
# outside Development both commands need --allow-production — take a backup first ("Backup and restore")
mc run --rm -v /opt/meydan/current/demo-assets:/demo-assets:ro migrate seed-demo --assets /demo-assets --allow-production
mc run --rm migrate remove-demo --allow-production
```

`remove-demo` deletes exactly the `IsDemo` rows and the files they own. A demo competition that meanwhile received a
real entry is kept (and reported), and so is anything real inside it.

## Firewall / exposure

Host firewall (ufw): 22, 80, 443 only. PostgreSQL and the API publish no port; nginx publishes 8040 on 127.0.0.1
only. Uploaded files are never reachable except through the API's authorisation checks (originals: owner/admin;
public derivatives: only while a result is published).
