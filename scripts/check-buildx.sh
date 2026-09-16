#!/usr/bin/env bash
set -euo pipefail
generated=$(docker buildx create --driver docker-container)
trap 'docker buildx rm "$generated" >/dev/null' EXIT
[[ "$generated" =~ ^[a-zA-Z][a-zA-Z0-9._-]*$ ]]
docker buildx inspect "$generated" >/dev/null
docker buildx rm "$generated" >/dev/null
trap - EXIT
explicit="quire-security-check-$(date +%s)-$$"
created=$(docker buildx create --driver docker-container --name "$explicit")
trap 'docker buildx rm "$created" >/dev/null' EXIT
[ "$created" = "$explicit" ]
docker buildx inspect "$created" >/dev/null
echo 'Automatic and explicit builder names passed CLI validation.'
