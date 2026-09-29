# Fate & Fridge — Deployment

Target environment: this household's **TrueNAS SCALE** server, with a generic
Docker host path described at the end.

## 1. Environment map

| Thing | Value |
| --- | --- |
| NAS host | `PlexNAS` (`192.168.0.2`) |
| Deployment root (ZFS dataset) | `/mnt/Data/calendarplanner` |
| SMB share | `\\192.168.0.2\cplanner` |
| Windows drive mapping | `Y:` |
| WSL mount | `/mnt/y` |
| SSH host alias | `fatefridge` → `CPlanner@192.168.0.2` |
| SSH key | `~/.ssh/fatefridge_deploy` |
| GitHub remote | `https://github.com/Tadbitnyght/fate-and-fridge` (private) |
| App URL | `http://192.168.0.2:8080/` (port configurable via `HOST_PORT`) |

The repository **is** the dataset: there is no separate copy on the NAS and no
file sync step. Deploying means running `docker compose` over SSH against this
checkout. Editing happens over SMB (Windows) or `/mnt/y` (WSL).

## 2. One-time access setup

### 2.1 SSH key (workstation)

The project uses its own key and host alias, separate from any other NAS
project. It was generated with:

```bash
ssh-keygen -t ed25519 -a 100 -f ~/.ssh/fatefridge_deploy -C "fate-and-fridge-deploy"
```

`~/.ssh/config` contains:

```sshconfig
Host fatefridge
    HostName 192.168.0.2
    User CPlanner
    IdentityFile ~/.ssh/fatefridge_deploy
    IdentitiesOnly yes
    StrictHostKeyChecking accept-new
    ConnectTimeout 10
    ServerAliveInterval 30
```

To rebuild this on another workstation, run the `ssh-keygen` command above,
recreate the config block, and install the new public key (next step).

### 2.2 Authorize the key for `CPlanner` (TrueNAS UI)

The `CPlanner` service account (uid 3002, home
`/mnt/Data/calendarplanner/home/CPlanner`) is the only account used for
deployment. Password authentication is disabled for it; pubkey only.

1. Print the public key: `cat ~/.ssh/fatefridge_deploy.pub`
2. TrueNAS UI → **Credentials → Users → CPlanner → Edit**
3. Paste the public key into the **SSH Public Key** field and save.

### 2.3 Grant Docker access

`CPlanner` is not an administrator by default and cannot reach the Docker
socket. In the same user edit screen, enable administrative/sudo privileges for
`CPlanner` (TrueNAS UI wording varies by version; enable sudo / admin role).

`scripts/deploy.sh` invokes `sudo docker compose`. It works with passwordless
sudo or with an interactive password prompt.

### 2.4 Verify

```bash
scripts/deploy.sh check
```

Expected output: the NAS hostname, the login name `CPlanner`, and
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
- `.gitattributes` normalizes all text to LF; `PLAN.md` still has CRLF in
  the working tree and will convert to LF the next time git touches it.

## 3. Configuration

Configuration lives in `.env` in the repository root (visible as `Y:\.env`
from Windows and `/mnt/y/.env` in WSL). It is gitignored.

```bash
cp .env.example .env
```

Set at minimum:

- `POSTGRES_PASSWORD` — generate with `openssl rand -hex 24`; keep it
  URL-safe (letters, digits, `-`, `_`) so the `DATABASE_URL` stays valid.
- `HOST_DATA_DIR=/mnt/Data/calendarplanner/data` — absolute path, persistent.
- `HOST_PORT` — change if another service occupies 8080.
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
ssh fatefridge
cd /mnt/Data/calendarplanner
sudo docker compose up -d --build     # first run: cp .env.example .env first
sudo docker compose ps
```

Migrations run automatically at container start (`docker-entrypoint.sh`, with
retries while PostgreSQL warms up). Health endpoints:

- App: `http://192.168.0.2:8080/api/v1/health`
- Database: `http://192.168.0.2:8080/api/v1/health/db`

## 5. Data layout and persistence

```
/mnt/Data/calendarplanner/
├── data/
│   ├── postgres/     PostgreSQL data directory
│   ├── config/       application config and secrets (recovery keys, etc.)
│   ├── images/       imported recipe images
│   └── backups/      application backup archives
├── docs/             project documentation
├── backend/          FastAPI backend
├── frontend/         React frontend
└── compose.yaml, Dockerfile, .env, ...
```

All of it is one ZFS dataset (`Data/calendarplanner`, 20.9 TB free).

**Do not open or edit PostgreSQL files over SMB.** For inspection use
`docker compose exec postgres psql ...` on the NAS.

**Snapshots:** configure a periodic snapshot task for the dataset to get
point-in-time protection (TrueNAS UI → Data Protection → Periodic Snapshot
Tasks). Application-level encrypted backups are a later milestone
(see `docs/MILESTONES.md`, Milestone 13).

## 6. Updating

1. Pull or edit the code (git over SMB/WSL, or directly on the NAS).
2. `scripts/deploy.sh up` — rebuilds, restarts, and applies migrations.

The database is untouched by rebuilds; it only changes through migrations.

## 7. Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| `permission denied ... docker API` over SSH | `CPlanner` lacks sudo/admin rights (step 2.3) |
| `Permission denied (publickey)` | Key from step 2.2 not installed, or wrong user in `~/.ssh/config` |
| `POSTGRES_PASSWORD is required` from compose | `.env` missing or the password line is empty |
| Port 8080 already in use | Change `HOST_PORT` in `.env`, then `deploy.sh up` |
| `password authentication failed for user` | `POSTGRES_PASSWORD` changed after the database volume was created; restore the old password or rotate it inside PostgreSQL |
| Migrations never finish | `deploy.sh logs`; check `DATABASE_URL` correctness and password URL-safety |
| App unhealthy in `deploy.sh ps` | `deploy.sh logs`; `/api/v1/health/db` shows database reachability |

## 8. Security notes and known limitations

- `.env`, runtime `data/`, and the `CPlanner` home directory are gitignored
  and must never be committed.
- PostgreSQL is not published to the host; only the app port is exposed, on
  the LAN by default. Do not expose it to the internet without TLS and
  authentication in front of it.
- The `~/.ssh/fatefridge_deploy` key carries sudo-capable access to the NAS.
  Treat it like a password; do not copy it into the repository.
- Known limitation (tracked for the hardening milestone): the application
  container currently runs as root, because bind-mounted storage on the NAS
  is owned by non-container users. Non-root execution needs a documented UID
  mapping strategy.

## 9. Generic Docker host

On any other Docker host, the standard flow applies:

```bash
git clone <repository>
cd fate-and-fridge
cp .env.example .env    # edit values; HOST_DATA_DIR can be any persistent path
docker compose up -d --build
```

The application is then reachable at `http://<host>:${HOST_PORT}/`.
