#!/usr/bin/env bash
set -euo pipefail
if [ "$(uname -s)" != Linux ] || [ "$(uname -m)" != x86_64 ]; then
  echo 'Supporting-tool binary pins currently target Linux amd64.' >&2; exit 2
fi
mode="${1:-all}"
case "$mode" in
  all) target=tools; image=collaborative-media-pipeline-supporting-tools:local ;;
  trivy) target=trivy-tools; image=collaborative-media-pipeline-trivy:local ;;
  *) echo 'Expected all or trivy.' >&2; exit 2 ;;
esac
mkdir -p .local/security-tools
docker build --target toolchain -f infra/images/supporting-tools.Dockerfile -t collaborative-media-pipeline-toolchain:local .
# Compile the two large tools sequentially to bound CI memory pressure.
docker build --target trivy-tools -f infra/images/supporting-tools.Dockerfile -t collaborative-media-pipeline-trivy:local .
if [ "$mode" = all ]; then
  docker build --target "$target" -f infra/images/supporting-tools.Dockerfile -t "$image" .
fi
container=$(docker create "$image" /trivy)
trap 'docker rm "$container" >/dev/null' EXIT
docker cp "$container:/trivy" .local/security-tools/trivy
grep '  .local/security-tools/trivy$' infra/tools/supporting-tools.sha256 | sha256sum -c -
chmod +x .local/security-tools/trivy
.local/security-tools/trivy --version
if [ "$mode" = all ]; then
  docker cp "$container:/docker-buildx" .local/security-tools/docker-buildx
  grep '  .local/security-tools/docker-buildx$' infra/tools/supporting-tools.sha256 | sha256sum -c -
  chmod +x .local/security-tools/docker-buildx
  plugin_dir="${DOCKER_CONFIG:-$HOME/.docker}/cli-plugins"
  mkdir -p "$plugin_dir"
  install -m 0755 .local/security-tools/docker-buildx "$plugin_dir/docker-buildx"
  docker buildx version
  curl --fail --location --retry 3 --output .local/security-tools/docker-compose \
    https://github.com/docker/compose/releases/download/v5.5.1/docker-compose-linux-x86_64
  grep '  .local/security-tools/docker-compose$' infra/tools/supporting-tools.sha256 | sha256sum -c -
  chmod +x .local/security-tools/docker-compose
  install -m 0755 .local/security-tools/docker-compose "$plugin_dir/docker-compose"
  docker compose version
fi
if [ -n "${GITHUB_PATH:-}" ]; then printf '%s\n' "$PWD/.local/security-tools" >> "$GITHUB_PATH"; fi
