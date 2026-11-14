#!/usr/bin/env bash
# CI, after a deploy: the API answers /health on its public URL, and the worker (private, so not
# called from here) serves the revision just deployed, which Cloud Run routes to only after its
# startup probe passed.
#
# Usage: infra/ci/smoke-deployed.sh <project ID> [region]

set -euo pipefail

project=${1:?usage: $0 <project ID> [region]}
region=${2:-europe-west1}

url=$(gcloud run services describe vision-api --project="$project" --region="$region" --format='value(status.url)')
curl -fsS -o /dev/null -w "%{http_code} $url/health\n" "$url/health"

read -r ready created < <(gcloud run services describe vision-worker --project="$project" --region="$region" \
  --format='value(status.latestReadyRevisionName,status.latestCreatedRevisionName)')
if [ -z "$ready" ] || [ "$ready" != "$created" ]; then
  echo "Error: vision-worker's latest revision $created isn't ready (ready: ${ready:-none})" >&2
  exit 1
fi
echo "vision-worker serves $ready."
