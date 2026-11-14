#!/usr/bin/env bash
# Deploys a Vision image that is already in Artifact Registry to one Cloud Run service, by
# digest (built once on main, run on staging, then promoted; nothing is built here). The service's
# shape (CPU, memory, scaling, identity, who may call it) is OpenTofu's (infra/envs); this script
# owns the image and the startup probe, as infra/README.md divides them. CI runs it; it can also be
# run by hand with the deployer's rights.

set -euo pipefail

usage() {
  cat <<'EOF'
Usage: infra/deploy.sh --service NAME --image REF --project ID [options]

Options:
  --service NAME   vision-api or vision-worker
  --image REF      Image by digest, e.g.
                   europe-west1-docker.pkg.dev/coachvision-prod/vision/api@sha256:<64 hex>
  --project ID     Google Cloud project ID (coachvision-staging or coachvision-prod)
  --region REGION  Cloud Run region (default europe-west1)
  --dry-run        Print the gcloud command and exit
  -h, --help       Show this help

A new revision gets traffic only once its /health answers 2xx (the startup probe); otherwise
the deploy fails and the previous revision keeps serving.
EOF
}

die() {
  echo "Error: $*" >&2
  exit 1
}

SERVICE=""
IMAGE=""
PROJECT=""
REGION="europe-west1"
DRY_RUN=false

while [ $# -gt 0 ]; do
  case "$1" in
    --service | --image | --project | --region)
      [ $# -ge 2 ] || die "$1 needs a value"
      case "$1" in
        --service) SERVICE=$2 ;;
        --image) IMAGE=$2 ;;
        --project) PROJECT=$2 ;;
        --region) REGION=$2 ;;
      esac
      shift
      ;;
    --dry-run) DRY_RUN=true ;;
    -h | --help)
      usage
      exit 0
      ;;
    *)
      usage >&2
      die "unknown option: $1"
      ;;
  esac
  shift
done

# The probe allows for how long each service takes to start. The worker checks and loads the
# pose model before it answers, so it gets two minutes; the API one.
case "$SERVICE" in
  vision-api) probe="httpGet.path=/health,initialDelaySeconds=0,timeoutSeconds=3,periodSeconds=5,failureThreshold=12" ;;
  vision-worker) probe="httpGet.path=/health,initialDelaySeconds=0,timeoutSeconds=3,periodSeconds=5,failureThreshold=24" ;;
  "") die "no service given; pass --service vision-api or --service vision-worker" ;;
  *) die "unknown service: $SERVICE (vision-api or vision-worker)" ;;
esac

[ -n "$PROJECT" ] || die "no project given; pass --project <project ID>"
[[ "$PROJECT" =~ ^[0-9]+$ ]] && die "--project is the project number; use the string project ID"
[[ "$PROJECT" =~ ^[a-z][a-z0-9-]{4,28}[a-z0-9]$ ]] || die "not a project ID: $PROJECT"
[[ "$REGION" =~ ^[a-z]+-[a-z]+[0-9]+$ ]] || die "not a region: $REGION"
# Only a digest pins what ran on staging; a tag could have moved.
image_re='^[a-z0-9.-]+(:[0-9]+)?/[a-z0-9._/-]+@sha256:[0-9a-f]{64}$'
[ -n "$IMAGE" ] || die "no image given; pass --image <registry>/<path>@sha256:<digest>"
[[ "$IMAGE" =~ $image_re ]] || die "--image must name an image by digest, not a tag: $IMAGE"

cmd=(
  gcloud run deploy "$SERVICE"
  --project="$PROJECT"
  --region="$REGION"
  --image="$IMAGE"
  --startup-probe="$probe"
  --quiet
)

if [ "$DRY_RUN" = true ]; then
  printf '%q ' "${cmd[@]}"
  printf '\n'
  exit 0
fi

command -v gcloud >/dev/null 2>&1 || die "gcloud CLI not found"
echo "Deploying $IMAGE as $SERVICE to $PROJECT ($REGION)."
"${cmd[@]}"
