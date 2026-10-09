#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"

exec 9> .deployment.lock
if ! flock -w 900 9; then
  echo "Baska bir Roomora dagitimi tamamlanmadi." >&2
  exit 1
fi

ENVIRONMENT="${1:?Kullanim: ./deploy-environment.sh <production|staging> <image-tag>}"
IMAGE_TAG="${2:?Kullanim: ./deploy-environment.sh <production|staging> <image-tag>}"
ENV_FILE=".env.server"
COMPOSE_FILE="compose.shared-server.yml"

if [[ "$ENVIRONMENT" != "production" && "$ENVIRONMENT" != "staging" ]]; then
  echo "Ortam production veya staging olmalidir." >&2
  exit 1
fi

if [[ "$ENVIRONMENT" == "production" ]]; then
  domain="${ROOMORA_PRODUCTION_DOMAIN:-api-roomora.takosware.com}"
else
  domain="${ROOMORA_STAGING_DOMAIN:-testapi-roomora.takosware.com}"
fi

if [[ ! "$IMAGE_TAG" =~ ^[A-Za-z0-9_.-]+$ ]]; then
  echo "Gecersiz image etiketi." >&2
  exit 1
fi

active_file=".active-${ENVIRONMENT}-slot"
current=""
if [[ -f "$active_file" ]]; then
  current="$(tr -d '[:space:]' < "$active_file")"
fi

if [[ "$current" == "blue" ]]; then
  target="green"
else
  target="blue"
fi

tag_key="ROOMORA_${ENVIRONMENT^^}_IMAGE_TAG"
if grep -q "^${tag_key}=" "$ENV_FILE"; then
  sed -i "s|^${tag_key}=.*|${tag_key}=${IMAGE_TAG}|" "$ENV_FILE"
else
  printf '%s=%s\n' "$tag_key" "$IMAGE_TAG" >> "$ENV_FILE"
fi

service="api-${ENVIRONMENT}-${target}"
docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" pull "$service" ocr
docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" up -d db ocr gateway

echo "${ENVIRONMENT} veritabani migration adimi calisiyor..."
docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" run \
  --rm --no-deps \
  -e Database__RunMigrations=true \
  "$service" --migrate-only

docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" up -d --no-deps "$service"
container_id="$(docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" ps -q "$service")"

for attempt in $(seq 1 40); do
  status="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$container_id")"
  if [[ "$status" == "healthy" ]]; then
    break
  fi
  if [[ "$status" == "unhealthy" || "$status" == "exited" || "$attempt" == "40" ]]; then
    docker logs "$container_id" --tail 150
    exit 1
  fi
  sleep 3
done

if grep -Eq "api-${ENVIRONMENT}-(blue|green):5118" Caddyfile.roomora; then
  sed -i -E "s#api-${ENVIRONMENT}-(blue|green):5118#api-${ENVIRONMENT}-${target}:5118#" Caddyfile.roomora
else
  echo "Gateway upstream kaydi bulunamadi." >&2
  exit 1
fi

docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" exec -T gateway \
  caddy validate --config - --adapter caddyfile < Caddyfile.roomora
docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" exec -T gateway \
  caddy reload --config - --adapter caddyfile < Caddyfile.roomora

if ! docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" exec -T gateway \
  wget -qO- --header="Host: ${domain}" http://127.0.0.1/health > /dev/null; then
  echo "Gateway saglik kontrolu basarisiz; eski yuva korunuyor." >&2
  if [[ -n "$current" && "$current" != "$target" ]]; then
    sed -i -E "s#api-${ENVIRONMENT}-(blue|green):5118#api-${ENVIRONMENT}-${current}:5118#" Caddyfile.roomora
    docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" exec -T gateway \
      caddy reload --config - --adapter caddyfile < Caddyfile.roomora
  fi
  docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" stop "$service"
  exit 1
fi

printf '%s\n' "$target" > "$active_file"

if [[ -n "$current" && "$current" != "$target" ]]; then
  sleep 15
  docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" stop "api-${ENVIRONMENT}-${current}"
fi

echo "${ENVIRONMENT} ${IMAGE_TAG} etiketiyle ${target} yuvasinda yayinda."
