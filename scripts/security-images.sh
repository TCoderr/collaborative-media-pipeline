#!/usr/bin/env bash
set -euo pipefail
# No ignores, no ignore-unfixed, no severity suppression, no continue-on-error.
if find . -path './node_modules' -prune -o -path './.local' -prune -o -name '.trivyignore*' -print | grep -q .; then
  echo 'Vulnerability suppression files are prohibited.' >&2; exit 1
fi
failed=0
for image in \
  collaborative-media-pipeline-api:local \
  collaborative-media-pipeline-worker:local \
  collaborative-media-pipeline-frontend:local \
  collaborative-media-pipeline-postgres:local \
  collaborative-media-pipeline-rabbitmq:local \
  collaborative-media-pipeline-storage-init:local \
  collaborative-media-pipeline-node-build:local \
  collaborative-media-pipeline-dotnet-build:local \
  collaborative-media-pipeline-terraform:local \
  collaborative-media-pipeline-toolchain:local \
  collaborative-media-pipeline-trivy:local \
  collaborative-media-pipeline-supporting-tools:local; do
  trivy image --scanners vuln --severity HIGH,CRITICAL --ignorefile /dev/null --ignore-unfixed=false --exit-code 1 "$image" || failed=1
done
trivy rootfs --format json --output .local/tool-audit.json --scanners vuln --severity HIGH,CRITICAL --ignorefile /dev/null --ignore-unfixed=false --exit-code 1 .local/security-tools || failed=1
python3 scripts/check-tool-audit.py .local/tool-audit.json || failed=1
exit "$failed"
