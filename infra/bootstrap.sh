#!/usr/bin/env bash
# What OpenTofu needs before it can run, kept outside OpenTofu so an apply
# can never lock itself out: the state bucket and, per project, the
# identities github-tofu-plan (read-only) and github-tofu (applies).
#
# Two passes for a brand-new project, because the two halves need each
# other: the state bucket this script makes is also the backend
# `versions.tf` needs for the very first `tofu apply`, and that apply is
# what creates the project's Workload Identity pool and provider (module
# `github-ci`, pool `github`, provider `github-actions`) that the second
# half of this script needs to trust. So run it once (APIs and the state
# bucket only; it stops there and says so), run `tofu init && tofu apply`
# in infra/envs/<environment>, then run it again (the state bucket step is
# a no-op the second time; it now also makes the two identities). For a
# project that already has both, one run does everything.
# Safe to run again either way; IAM grants are only ever added.
# --dry-run prints every changing gcloud command instead of running it, for
# both passes (it can't tell which pass a real run would need, since it
# reads nothing).

set -euo pipefail

BUCKET_LOCATION=europe-west1
POOL=github
PROVIDER=github-actions
# Repository IvanTachev233/CoachVisionApi's immutable OIDC subject prefix
# (gh api repos/IvanTachev233/CoachVisionApi/actions/oidc/customization/sub).
DEFAULT_GITHUB_SUB_PREFIX="repo:IvanTachev233@63864892/CoachVisionApi@1457436014"
# Saved plans can hold values in cleartext, so they don't stay around.
PLAN_RETENTION_DAYS=14
# Older copies of the state kept for recovery.
STATE_VERSIONS_KEPT=30

usage() {
  cat <<'EOF'
Usage: infra/bootstrap.sh --environment staging|production [--dry-run]

One-time setup for OpenTofu in one Vision project: coachvision-staging or
coachvision-prod. Run it with --dry-run first.

On a brand-new project this takes two runs: this first one only enables
the APIs and makes the state bucket, then stops (OpenTofu's backend needs
that bucket to exist before its first apply, which is what then creates
the Workload Identity pool and provider, module github-ci, that this
script's second run needs). Run `tofu init && tofu apply` in
infra/envs/<environment>, then run this script again to make the two
GitHub Actions identities. On the two existing projects the provider is
already there, so one run does everything.

Options:
  --environment NAME     staging or production
  --github-sub-prefix PREFIX
                          Start of CoachVisionApi's OIDC sub claim (default:
                          the known immutable prefix), e.g.
                          repo:owner@123/name@456
  --dry-run               Print the changing commands instead of running
                          them; needs no login
  -h, --help              Show this help
EOF
}

die() {
  echo "Error: $*" >&2
  exit 1
}

DRY_RUN=false
GITHUB_SUB_PREFIX=$DEFAULT_GITHUB_SUB_PREFIX
while [ $# -gt 0 ]; do
  case "$1" in
    --environment)
      [ $# -ge 2 ] || die "$1 needs a value"
      ENVIRONMENT=$2
      shift
      ;;
    --environment=*) ENVIRONMENT=${1#*=} ;;
    --github-sub-prefix)
      [ $# -ge 2 ] || die "$1 needs a value"
      GITHUB_SUB_PREFIX=$2
      shift
      ;;
    --github-sub-prefix=*) GITHUB_SUB_PREFIX=${1#*=} ;;
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

[[ "$GITHUB_SUB_PREFIX" =~ ^repo:[^:[:space:]]+$ ]] ||
  die "--github-sub-prefix must look like repo:owner/name (no trailing colon): $GITHUB_SUB_PREFIX"

# APIs the provider calls. google_project_service takes over in OpenTofu, but
# the first plan can't run without these. pubsub: the storage/queue roots add
# Pub/Sub topics and subscriptions.
apis=(cloudresourcemanager.googleapis.com serviceusage.googleapis.com storage.googleapis.com
  iam.googleapis.com iamcredentials.googleapis.com sts.googleapis.com run.googleapis.com
  secretmanager.googleapis.com monitoring.googleapis.com pubsub.googleapis.com)
case "${ENVIRONMENT:-}" in
  staging)
    PROJECT=coachvision-staging
    STAGING_PROJECT=
    ;;
  production)
    PROJECT=coachvision-prod
    # Production's root reads staging's project (data "google_project"
    # "staging"), so production's two identities need to read it too.
    STAGING_PROJECT=coachvision-staging
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

# Under --dry-run everything counts as missing, so the output shows every create.
exists() {
  [ "$DRY_RUN" = false ] && "$@" >/dev/null 2>&1
}

# IAM is eventually consistent: a service account created a moment ago may not
# be found yet, so retry with backoff (about 2.5 minutes in all).
grant() {
  local delay
  if [ "$DRY_RUN" = true ]; then
    run "$@" --quiet
    return
  fi
  for delay in 5 10 20 40 80; do
    "$@" --quiet >/dev/null 2>&1 && return 0
    echo "Grant failed ($*); retrying in ${delay}s..." >&2
    sleep "$delay"
  done
  "$@" --quiet >/dev/null
}

if [ "$DRY_RUN" = true ]; then
  project_number="<number-of-$PROJECT>"
else
  command -v gcloud >/dev/null 2>&1 || die "gcloud CLI not found"
  project_number="$(gcloud projects describe "$PROJECT" --format='value(projectNumber)')" ||
    die "can't read project $PROJECT; check your gcloud login"
fi

subject_member() {
  echo "principal://iam.googleapis.com/projects/$project_number/locations/global/workloadIdentityPools/$POOL/subject/$GITHUB_SUB_PREFIX:$1"
}

bucket="$PROJECT-tfstate"
plan_sa="github-tofu-plan@$PROJECT.iam.gserviceaccount.com"
apply_sa="github-tofu@$PROJECT.iam.gserviceaccount.com"
github_environment="infra-$ENVIRONMENT"

echo "Bootstrapping OpenTofu for $ENVIRONMENT in $PROJECT."

run gcloud services enable "${apis[@]}" --project="$PROJECT"

if exists gcloud storage buckets describe "gs://$bucket"; then
  echo "Bucket gs://$bucket exists; versioning and lifecycle are re-applied."
else
  echo "Creating the state bucket gs://$bucket ($BUCKET_LOCATION)..."
  run gcloud storage buckets create "gs://$bucket" --project="$PROJECT" \
    --location="$BUCKET_LOCATION" --uniform-bucket-level-access --public-access-prevention
fi

# Saved plans go after 14 days; of the state, the newest 30 older copies stay.
lifecycle_file="$(mktemp)"
trap 'rm -f "$lifecycle_file"' EXIT
printf '{"rule":[{"action":{"type":"Delete"},"condition":{"age":%s,"matchesPrefix":["plans/"]}},{"action":{"type":"Delete"},"condition":{"isLive":false,"numNewerVersions":%s}}]}\n' \
  "$PLAN_RETENTION_DAYS" "$STATE_VERSIONS_KEPT" >"$lifecycle_file"
[ "$DRY_RUN" = false ] || echo "  lifecycle: $(cat "$lifecycle_file")"
run gcloud storage buckets update "gs://$bucket" --versioning --lifecycle-file="$lifecycle_file"

# Second pass starts here: module github-ci's Workload Identity pool and
# provider must exist before the two CI identities can be trusted to
# impersonate from it. On a brand-new project (no `tofu apply` yet) this
# is expected to be missing on the first run; stop successfully rather
# than fail, since the state bucket above is everything a first apply
# needs. --dry-run can't read whether it exists, and reads nothing, so it
# always carries on to print the identities' commands too.
if [ "$DRY_RUN" = false ]; then
  if ! gcloud iam workload-identity-pools providers describe "$PROVIDER" --project="$PROJECT" \
    --location=global --workload-identity-pool="$POOL" >/dev/null 2>&1; then
    echo
    echo "State bucket gs://$bucket is ready; provider $PROVIDER not found in $PROJECT yet."
    echo "Run 'tofu init && tofu apply' in infra/envs/$ENVIRONMENT (module github-ci makes it),"
    echo "then run this script again to make github-tofu-plan and github-tofu."
    exit 0
  fi
fi

create_sa() {
  local name=$1 description=$2
  if exists gcloud iam service-accounts describe "$name@$PROJECT.iam.gserviceaccount.com" \
    --project="$PROJECT"; then
    echo "Service account $name exists; left unchanged."
  else
    echo "Creating the service account $name..."
    run gcloud iam service-accounts create "$name" --project="$PROJECT" \
      --display-name="$name" --description="$description"
  fi
}

project_grant() {
  grant gcloud projects add-iam-policy-binding "$PROJECT" \
    --member="serviceAccount:$1" --role="$2" --condition=None
}

impersonation_grant() {
  grant gcloud iam service-accounts add-iam-policy-binding "$1" --project="$PROJECT" \
    --role=roles/iam.workloadIdentityUser --member="$(subject_member "$2")"
}

# --- Read-only: plans on pull requests and on main ---------------------------
# Reads no secret values: Viewer lets it see secrets' metadata only. But
# Viewer also reads the objects of any bucket that still has Cloud Storage's
# default project-wide bindings (legacyObjectReader for projectViewer on a
# uniform-access bucket), and this identity is usable from any same-repo
# pull request. That's why module `pipeline`'s media buckets carry an
# authoritative IAM policy that replaces those defaults (ADR 0030); this
# state bucket is meant to be readable by this identity. Any bucket added
# later that holds private data must get the same authoritative policy, or
# this read-only, PR-triggerable identity can read it too.
create_sa github-tofu-plan "Runs tofu plan from GitHub Actions, read-only (ADR 0024)"
for role in roles/viewer roles/iam.securityReviewer roles/storage.bucketViewer \
  roles/serviceusage.serviceUsageConsumer; do
  project_grant "$plan_sa" "$role"
done
# --condition=None: once the conditional binding below exists (any run after
# the first), gcloud refuses to add a binding without saying which it is.
grant gcloud storage buckets add-iam-policy-binding "gs://$bucket" \
  --member="serviceAccount:$plan_sa" --role=roles/storage.objectViewer --condition=None
# It writes saved plans under plans/ and nothing else, so never the state.
grant gcloud storage buckets add-iam-policy-binding "gs://$bucket" \
  --member="serviceAccount:$plan_sa" --role=roles/storage.objectCreator \
  --condition="expression=resource.name.startsWith('projects/_/buckets/$bucket/objects/plans/'),title=plans-only,description=Saved plans only"
# Production's plan reads staging's project (data "google_project" "staging");
# Browser is enough to read a project's number, nothing else in it.
if [ -n "$STAGING_PROJECT" ]; then
  grant gcloud projects add-iam-policy-binding "$STAGING_PROJECT" \
    --member="serviceAccount:$plan_sa" --role=roles/browser --condition=None
fi
impersonation_grant "$plan_sa" pull_request
impersonation_grant "$plan_sa" ref:refs/heads/main

# --- Applies: only jobs in this project's GitHub environment ----------------
# Listed roles rather than Owner. Together they are close to it (IAM admin can
# grant more); the environment's branch rule and reviewer are what guard them.
create_sa github-tofu "Runs tofu apply from GitHub Actions in $github_environment (ADR 0024)"
apply_roles=(roles/run.admin roles/iam.serviceAccountAdmin
  # Project-wide actAs: the Cloud Run runtime identities and the push
  # subscription's invoker (vision-worker-invoker) are created by this same
  # identity in the same apply, so a grant scoped to one resource (as
  # LiftBig's bootstrap does for its pre-existing runtime account) can't be
  # made ahead of time here.
  roles/iam.serviceAccountUser
  roles/iam.workloadIdentityPoolAdmin roles/resourcemanager.projectIamAdmin
  roles/secretmanager.admin roles/storage.admin roles/pubsub.admin roles/monitoring.admin
  roles/serviceusage.serviceUsageAdmin)
[ "$ENVIRONMENT" != production ] || apply_roles+=(roles/artifactregistry.admin)
for role in "${apply_roles[@]}"; do
  project_grant "$apply_sa" "$role"
done
if [ -n "$STAGING_PROJECT" ]; then
  grant gcloud projects add-iam-policy-binding "$STAGING_PROJECT" \
    --member="serviceAccount:$apply_sa" --role=roles/browser --condition=None
fi
impersonation_grant "$apply_sa" "environment:$github_environment"

provider_name="projects/$project_number/locations/global/workloadIdentityPools/$POOL/providers/$PROVIDER"
echo
if [ "$DRY_RUN" = true ]; then
  echo "Dry run: nothing was changed."
else
  echo "Done: gs://$bucket, $plan_sa and $apply_sa."
fi
echo
echo "GitHub → Settings → Environments → $github_environment → variables:"
echo "  TOFU_WIF_PROVIDER=$provider_name"
echo "  TOFU_SA=$apply_sa"
echo "Repository variables (Settings → Secrets and variables → Actions → Variables):"
suffix="$(printf '%s' "$ENVIRONMENT" | tr '[:lower:]' '[:upper:]')"
echo "  TOFU_PLAN_WIF_PROVIDER_$suffix=$provider_name"
echo "  TOFU_PLAN_SA_$suffix=$plan_sa"
