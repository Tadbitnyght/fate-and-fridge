# Fate & Fridge — Deployment

Target environment: **TrueNAS SCALE** (the reference deployment), with a generic
Docker host path described at the end.

## 1. Environment map

| Thing | Value |
| --- | --- |
| NAS host | `nas` (`nas.local`) |
| Deployment root (ZFS dataset) | `/mnt/pool/apps/fate-and-fridge` |
| SMB share | `\\nas.local\fate-and-fridge` |
| Windows drive mapping | `Y:` |
| WSL mount | `/mnt/y` |
| SSH host alias | `fate-and-fridge` → `deploy@nas.local` |
| SSH key | `~/.ssh/fate-and-fridge_deploy` |
| GitHub remote | `https://github.com/Tadbitnyght/fate-and-fridge` |
| App URL | `http://nas.local:50080/` (port configurable via `HOST_PORT`) |

The repository **is** the dataset: there is no separate copy on the NAS and no
file sync step. Deploying means running `docker compose` over SSH against this
checkout. Editing happens over SMB (Windows) or `/mnt/y` (WSL).

## 2. One-time access setup

### 2.1 SSH key (workstation)

The project uses its own key and host alias, separate from any other NAS
project. It was generated with:

```bash
ssh-keygen -t ed25519 -a 100 -f ~/.ssh/fate-and-fridge_deploy -C "fate-and-fridge-deploy"
```

`~/.ssh/config` contains:

```sshconfig
Host fate-and-fridge
    HostName nas.local
    User deploy
    IdentityFile ~/.ssh/fate-and-fridge_deploy
    IdentitiesOnly yes
    StrictHostKeyChecking accept-new
    ConnectTimeout 10
    ServerAliveInterval 30
```

To rebuild this on another workstation, run the `ssh-keygen` command above,
recreate the config block, and install the new public key (next step).

### 2.2 Authorize the key for `deploy` (TrueNAS UI)

The `deploy` service account (<deploy-uid>, home
`/mnt/pool/apps/fate-and-fridge/home/deploy`) is the only account used for
deployment. Password authentication is disabled for it; pubkey only.

1. Print the public key: `cat ~/.ssh/fate-and-fridge_deploy.pub`
2. TrueNAS UI → **Credentials → Users → deploy → Edit**
3. Paste the public key into the **SSH Public Key** field and save.

### 2.3 Grant Docker access

`deploy` is not an administrator and cannot reach the Docker socket by
default. It holds passwordless sudo for the Docker binary only:

```text
(ALL) NOPASSWD: /usr/bin/docker
```

`scripts/deploy.sh` relies on exactly that (`sudo docker compose ...`) and
needs no further privileges. Confirm the rule on the NAS with `sudo -n -l`.

Additional TrueNAS roles (for example `SNAPSHOT_TASK_WRITE`, needed to create
periodic snapshot tasks) are separate and optional — see section 5.

### 2.4 Verify

```bash
scripts/deploy.sh check
```

Expected output: the NAS hostname, the login name `deploy`, and
`Docker Compose version v2.x`. If SSH fails with `Permission denied
(publickey)`, the key from step 2.2 is missing.

### 2.5 Git notes on this share (SMB/9p quirks)

The SMB mount does not support `chmod`, which affects a few git commands:

- The repository was initialized on a local filesystem and its `.git`
  directory copied onto the share, because `git init` performs a `chmod`
  probe that the mount rejects.
- This workstation already has `safe.directory=/mnt/y` (files report as
  owned by `root` over 9p) and the repo uses `core.filemode=false`.
- `git add`, `commit`, `push`, `fetch`, `log`, `status` work normally.
- Commands that rewrite `.git/config` (`git remote add`, `git config`,
  `git push -u`) fail with `chmod on .git/config.lock failed`. Edit
  `.git/config` directly, or run those commands on the NAS side.
- MSBuild cannot set file timestamps on the share either. Export
  `FATE_ARTIFACTS_PATH=/tmp/fate-and-fridge-artifacts` before running
  `dotnet` commands so build outputs stay on a local filesystem.

## 3. Configuration

Configuration lives in `.env` in the repository root (visible as `Y:\.env`
from Windows and `/mnt/y/.env` in WSL). It is gitignored.

```bash
cp .env.example .env
```

Set at minimum:

- `POSTGRES_PASSWORD` — generate with `openssl rand -hex 24`; keep it
  URL-safe (letters, digits, `-`, `_`) so it embeds cleanly in the
  PostgreSQL connection string compose builds.
- `HOST_DATA_DIR=/mnt/pool/apps/fate-and-fridge/data` — absolute path, persistent.
- `HOST_PORT` — change if another service occupies 50080.
- `TZ` — household timezone (IANA name), used for calendar-day semantics.

## 4. Deploying and operating

| Task | Command |
| --- | --- |
| Build + (re)start the stack | `scripts/deploy.sh up` |
| Container status | `scripts/deploy.sh ps` |
| Follow app logs | `scripts/deploy.sh logs` |
| Apply migrations manually | `scripts/deploy.sh migrate` |
| Restart without rebuild | `scripts/deploy.sh restart` |
| Stop (data preserved) | `scripts/deploy.sh down` |
| Shell on the NAS | `scripts/deploy.sh ssh` |

Manual equivalent, if you prefer plain SSH:

```bash
ssh fate-and-fridge
cd /mnt/pool/apps/fate-and-fridge
sudo docker compose up -d --build     # first run: cp .env.example .env first
sudo docker compose ps
```

Migrations run automatically at container start
(`Database__MigrateOnStartup=true` in `compose.yaml`, with retries while
PostgreSQL warms up). `--migrate` now also backfills the default reference data
once (idempotent). Health endpoints:

- App: `http://nas.local:50080/api/v1/health`
- Database: `http://nas.local:50080/api/v1/health/db`

End-to-end smoke tests can run against the live deployment:

```bash
cd frontend
npx playwright install chromium            # first time only
E2E_BASE_URL=http://nas.local:50080 npm run test:e2e
```

### Image contents

The runtime stage is `mcr.microsoft.com/dotnet/aspnet:10.0`, which currently
resolves to **Ubuntu 24.04 (noble)** — *not* Debian. The image installs
`postgresql-client-17` from the PGDG suite resolved from `/etc/os-release`
(`noble-pgdg` today) and asserts at build time that `pg_dump`, `pg_restore`, and
`psql` report **17.x**. A base-image change to a codename PGDG does not publish
therefore fails the build instead of silently shipping an older client — and an
older `pg_dump` cannot dump the PostgreSQL 17 server, which would make every
backup broken.

Verified: client **17.11**, healthcheck `curl` intact; the client tools account
for about 63 MB of the image.

## 5. Data layout and persistence

```
/mnt/pool/apps/fate-and-fridge/
├── data/
│   ├── postgres/     PostgreSQL data directory
│   ├── config/       application config and secrets (recovery keys, etc.)
│   ├── images/       imported recipe images
│   └── backups/      application backup archives
├── docs/             project documentation
├── backend/          ASP.NET Core backend
├── frontend/         React frontend
└── compose.yaml, Dockerfile, .env, ...
```

All of it is one ZFS dataset (`Data/calendarplanner`, 20.9 TB free).

**Do not open or edit PostgreSQL files over SMB.** For inspection use
`docker compose exec postgres psql ...` on the NAS.

**Snapshots:** configure a periodic snapshot task for the dataset to get
point-in-time protection. Recommended values for this dataset:

| Setting | Value |
| --- | --- |
| Dataset | `Data/calendarplanner` |
| Recursive | off |
| Lifetime | 14 days |
| Naming schema | `auto-%Y-%m-%d_%H-%M` |
| Schedule | daily at 03:00 |
| Enabled | yes |

TrueNAS UI: **Data Protection → Periodic Snapshot Tasks → Add**.

Equivalent one-liner (run as an admin, from the NAS shell or with sudo):

```bash
midclt call pool.snapshottask.create \
  '{"dataset": "Data/calendarplanner", "recursive": false,
    "lifetime_value": 14, "lifetime_unit": "DAY",
    "naming_schema": "auto-%Y-%m-%d_%H-%M",
    "schedule": {"minute": "00", "hour": "03", "dom": "*", "month": "*", "dow": "*"},
    "enabled": true}'
```

The `deploy` deploy account manages snapshot tasks through `sudo midclt`
(sudo runs as root, which satisfies the API's `SNAPSHOT_TASK_WRITE`
requirement). A `SNAPSHOT_TASK_WRITE` role grant is the alternative for
non-sudo access.

To trigger the task immediately (useful for verification), call it without
`-j` — `midclt call -j pool.snapshottask.run <id>` keeps the SSH session open
after the job completes:

```bash
midclt call pool.snapshottask.run <task-id>
# then confirm the new snapshot:
zfs list -t snapshot -o name,creation | grep calendarplanner
```

Application-level encrypted backups are covered in `docs/SECURITY.md` §11.

## 6. Updating

1. Pull or edit the code (git over SMB/WSL, or directly on the NAS).
2. `scripts/deploy.sh up` — rebuilds, restarts, and applies migrations.

The database is untouched by rebuilds; it only changes through migrations.

## 7. Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| `permission denied ... docker API` over SSH | `deploy` lacks sudo/admin rights (step 2.3) |
| `Permission denied (publickey)` | Key from step 2.2 not installed, or wrong user in `~/.ssh/config` |
| `POSTGRES_PASSWORD is required` from compose | `.env` missing or the password line is empty |
| Port 50080 already in use | Change `HOST_PORT` in `.env`, then `deploy.sh up` |
| `password authentication failed for user` | `POSTGRES_PASSWORD` changed after the database volume was created; restore the old password or rotate it inside PostgreSQL |
| Migrations never finish | `deploy.sh logs`; check the `ConnectionStrings__Default` host/credentials and password URL-safety |
| App unhealthy in `deploy.sh ps` | `deploy.sh logs`; `/api/v1/health/db` shows database reachability |

## 8. Security notes and known limitations

- `.env`, runtime `data/`, and the `deploy` home directory are gitignored
  and must never be committed.
- PostgreSQL is not published to the host; only the app port is exposed, on
  the LAN by default. Do not expose it to the internet without TLS and
  authentication in front of it.
- The `~/.ssh/fate-and-fridge_deploy` key carries sudo-capable access to the NAS.
  Treat it like a password; do not copy it into the repository.
- Known limitation: the application
  container currently runs as root, because bind-mounted storage on the NAS
  is owned by non-container users. Non-root execution needs a documented UID
  mapping strategy.

## 9. Workstation Docker access (TrueNAS Docker context)

The workstation can also drive the NAS Docker daemon over SSH, so Docker
commands can be run against the NAS from a workstation.

The socket itself is root-only, so `deploy` runs it through its
`NOPASSWD: /usr/bin/docker` rule via a small wrapper on the NAS:

```sh
# /mnt/pool/apps/fate-and-fridge/home/deploy/bin/docker
#!/bin/sh
exec sudo -n /usr/bin/docker "$@"
```

with `export PATH="$HOME/bin:$PATH"` in `~/.zshenv` so the wrapper is used by
non-interactive SSH commands (`docker system dial-stdio`).

On the workstation the context is:

```bash
docker context create truenas \
  --description "TrueNAS SCALE (nas) via SSH" \
  --docker "host=ssh://fate-and-fridge"

docker --context truenas ps        # NAS containers
docker context use truenas         # make it the default (optional)
docker context use default         # back to the local WSL daemon
```

Notes:
- Containers started through this context run on the NAS, not locally.
- If commands fail with `permission denied ... docker API`, re-check the
  wrapper and the sudo rule (`sudo -n -l` on the NAS).

## 10. Generic Docker host

On any other Docker host, the standard flow applies:

```bash
git clone <repository>
cd fate-and-fridge
cp .env.example .env    # edit values; HOST_DATA_DIR can be any persistent path
docker compose up -d --build
```

The application is then reachable at `http://<host>:${HOST_PORT}/`.
