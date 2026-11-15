#!/usr/bin/env bash
# CI: builds one Vision image and checks that it starts and answers /health. With --push (main
# only) the image is pushed once per git tree of its folder (`tree-<hash>` tag, the folder's whole
# build input): a tree already in the registry is reused, not rebuilt, so staging and production
# run the same digest. Writes `image=<repo>@sha256:...` to GITHUB_OUTPUT when pushing.
#
# Usage: infra/ci/build-image.sh <api|worker> [--push <registry>]
#   e.g. infra/ci/build-image.sh worker --push europe-west1-docker.pkg.dev/coachvision-prod/vision

set -euo pipefail

die() {
  echo "Error: $*" >&2
  exit 1
}

component=${1:-}
case "$component" in
  api | worker) ;;
  *) die "usage: $0 <api|worker> [--push <registry>]" ;;
esac
shift
registry=""
if [ "${1:-}" = --push ]; then
  [ $# -ge 2 ] || die "--push needs a registry"
  registry=$2
fi

root=$(git rev-parse --show-toplevel)
tree=$(git -C "$root" rev-parse "HEAD:$component")
local_tag="vision-$component:check"

# Starts the image as Cloud Run would (port 8080, its own user) and waits up to two minutes for
# /health, the startup probe's path. The worker answers only after loading the pose model.
smoke() {
  local name="smoke-$component" ok=false
  docker run -d --rm --name "$name" -p 127.0.0.1:18080:8080 "$local_tag" >/dev/null
  for _ in $(seq 1 60); do
    if curl -fsS -o /dev/null http://127.0.0.1:18080/health 2>/dev/null; then
      ok=true
      break
    fi
    sleep 2
  done
  [ "$ok" = true ] && curl -fsS http://127.0.0.1:18080/health && echo
  docker logs "$name" 2>&1 | tail -n 20
  docker stop "$name" >/dev/null
  [ "$ok" = true ] || die "vision-$component didn't answer /health within two minutes"
  echo "vision-$component answers /health."
}

build() {
  docker build --target production -t "$local_tag" "$root/$component"
  smoke
}

if [ -z "$registry" ]; then
  build
  exit 0
fi

repo="$registry/$component"
sha=${GITHUB_SHA:-$(git -C "$root" rev-parse HEAD)}
digest_of() {
  gcloud artifacts docker images describe "$1" --format='value(image_summary.digest)'
}

gcloud auth configure-docker "${registry%%/*}" --quiet
if digest=$(digest_of "$repo:tree-$tree" 2>/dev/null) && [ -n "$digest" ]; then
  echo "Reusing $repo:tree-$tree ($digest); not rebuilt."
  gcloud artifacts docker tags add "$repo@$digest" "$repo:$sha" --quiet
else
  build
  docker tag "$local_tag" "$repo:tree-$tree"
  docker tag "$local_tag" "$repo:$sha"
  docker push --all-tags "$repo"
  digest=$(digest_of "$repo:tree-$tree")
fi
[[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]] || die "no digest for $repo:tree-$tree: '$digest'"
echo "image=$repo@$digest" | tee -a "${GITHUB_OUTPUT:-/dev/null}"
