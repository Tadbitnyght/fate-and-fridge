#!/usr/bin/env bash
#
# Fate & Fridge — deploy/operate the TrueNAS stack over SSH.
#
# The repository lives on the NAS itself (shared over SMB and edited from
# Windows/WSL), so "deploy" means running docker compose against this
# checkout over SSH. No files are copied.
#
# See docs/DEPLOYMENT.md for the one-time access setup.
set -euo pipefail

DEPLOY_HOST="${DEPLOY_HOST:-fatefridge}"
DEPLOY_DIR="${DEPLOY_DIR:-/mnt/Data/calendarplanner}"

usage() {
  cat <<'EOF'
Usage: scripts/deploy.sh [command]

Commands:
  up        Build images and (re)start the stack (default)
  restart   Restart running containers
  ps        Show container status
  logs      Follow application logs
  migrate   Apply pending database migrations in the app container
  down      Stop and remove containers (persistent data is preserved)
  check     Verify SSH access and Docker permissions on the NAS
  ssh       Open an interactive shell on the NAS
  help      Show this help

Environment overrides:
  DEPLOY_HOST   SSH host alias        (default: fatefridge)
  DEPLOY_DIR    Repo path on the NAS  (default: /mnt/Data/calendarplanner)
EOF
}

run_remote() {
  ssh -t "$DEPLOY_HOST" "cd '$DEPLOY_DIR' && $1"
}

command="${1:-up}"

case "$command" in
  up)
    run_remote "sudo docker compose up -d --build"
    ;;
  restart)
    run_remote "sudo docker compose restart"
    ;;
  ps)
    run_remote "sudo docker compose ps"
    ;;
  logs)
    run_remote "sudo docker compose logs -f --tail=200 app"
    ;;
  migrate)
    run_remote "sudo docker compose exec app dotnet FateAndFridge.Api.dll --migrate"
    ;;
  down)
    run_remote "sudo docker compose down"
    ;;
  check)
    ssh -t "$DEPLOY_HOST" "echo \"SSH OK on \$(hostname) as \$(whoami)\"; cd '$DEPLOY_DIR' && sudo docker compose version"
    ;;
  ssh)
    exec ssh -t "$DEPLOY_HOST" "cd '$DEPLOY_DIR' && exec \"\$SHELL\" -l"
    ;;
  help|-h|--help)
    usage
    ;;
  *)
    echo "Unknown command: $command" >&2
    echo
    usage
    exit 1
    ;;
esac
