#!/usr/bin/env bash
# What OpenTofu needs before it can run, kept outside OpenTofu so an apply can
# never lock itself out: the APIs the first plan calls and the state bucket.
# Safe to run again. --dry-run prints every changing gcloud command instead.

set -euo pipefail

BUCKET_LOCATION=europe-west1
# Older copies of the state kept for recovery.
STATE_VERSIONS_KEPT=30

usage() {
  cat <<'EOF'
Usage: infra/bootstrap.sh --environment staging|production [--dry-run]

One-time setup for OpenTofu in one Vision project: coachvision-staging or
coachvision-prod. Run it with --dry-run first.

Options:
  --environment NAME  staging or production
  --dry-run           Print the changing commands instead of running them;
                      needs no login
  -h, --help          Show this help
EOF
}

die() {
  echo "Error: $*" >&2
  exit 1
}

DRY_RUN=false
while [ $# -gt 0 ]; do
  case "$1" in
    --environment)
      [ $# -ge 2 ] || die "$1 needs a value"
      ENVIRONMENT=$2
      shift
      ;;
    --environment=*) ENVIRONMENT=${1#*=} ;;
    --dry-run) DRY_RUN=true ;;
    -h | --help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
  shift
done

apis=(cloudresourcemanager.googleapis.com serviceusage.googleapis.com storage.googleapis.com
  iam.googleapis.com iamcredentials.googleapis.com sts.googleapis.com run.googleapis.com
  secretmanager.googleapis.com monitoring.googleapis.com)
case "${ENVIRONMENT:-}" in
  staging) PROJECT=coachvision-staging ;;
  production)
    PROJECT=coachvision-prod
    apis+=(artifactregistry.googleapis.com)
    ;;
  *) die "--environment must be staging or production" ;;
esac

# Changing gcloud calls go through run: printed under --dry-run, run otherwise.
run() {
  if [ "$DRY_RUN" = true ]; then
    printf '+'
    printf ' %q' "$@"
    printf '\n'
  else
    "$@"
  fi
}

if [ "$DRY_RUN" = false ]; then
  command -v gcloud >/dev/null 2>&1 || die "gcloud CLI not found"
  gcloud projects describe "$PROJECT" --format='value(projectId)' >/dev/null ||
    die "can't read project $PROJECT; create it (with billing) first and check your gcloud login"
fi

bucket="$PROJECT-tfstate"
echo "Bootstrapping OpenTofu for $ENVIRONMENT in $PROJECT."

run gcloud services enable "${apis[@]}" --project="$PROJECT"

if [ "$DRY_RUN" = false ] && gcloud storage buckets describe "gs://$bucket" >/dev/null 2>&1; then
  echo "Bucket gs://$bucket exists; versioning and lifecycle are re-applied."
else
  echo "Creating the state bucket gs://$bucket ($BUCKET_LOCATION)..."
  run gcloud storage buckets create "gs://$bucket" --project="$PROJECT" \
    --location="$BUCKET_LOCATION" --uniform-bucket-level-access --public-access-prevention
fi

# Of the state, the newest 30 older copies stay.
lifecycle_file="$(mktemp)"
trap 'rm -f "$lifecycle_file"' EXIT
printf '{"rule":[{"action":{"type":"Delete"},"condition":{"isLive":false,"numNewerVersions":%s}}]}\n' \
  "$STATE_VERSIONS_KEPT" >"$lifecycle_file"
[ "$DRY_RUN" = false ] || echo "  lifecycle: $(cat "$lifecycle_file")"
run gcloud storage buckets update "gs://$bucket" --versioning --lifecycle-file="$lifecycle_file"

echo
if [ "$DRY_RUN" = true ]; then
  echo "Dry run: nothing was changed."
else
  echo "Done: gs://$bucket. Next: tofu init and plan in infra/envs/$ENVIRONMENT."
fi
