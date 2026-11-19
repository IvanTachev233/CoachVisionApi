# Infrastructure

Google Cloud for Vision, managed with OpenTofu. Two projects, `coachvision-staging` and `coachvision-prod`, each a root in `envs/` with its state in that project's `<project>-tfstate` bucket. The modules are LiftBig's (`service`, `github-ci`, `monitoring`), read from the public LiftBig repo at a tag (`ref` in each root's `locals`); move both roots to a new tag together.

This repository is public: no secrets, personal email addresses or billing ids in code, and no plan output in CI logs.

- `bootstrap.sh` makes what OpenTofu needs before it can run: the APIs the first plan calls and the state bucket, and then, once module `github-ci` has been applied in that project, the two GitHub Actions identities `github-tofu-plan` (read-only) and `github-tofu` (applies), each trusted only from the immutable OIDC subject it needs (a pull request or `ref:refs/heads/main` for the plan identity, this project's `infra-<environment>` GitHub environment for the apply identity). A brand-new project needs it twice: once for the bucket (OpenTofu's backend needs it before the first `tofu apply`, which is what creates `github-ci`'s pool and provider), then `tofu apply`, then the script again for the two identities; the two existing projects already have the provider, so one run does everything. It stays outside OpenTofu so an apply can't lock itself out.
- `github-tofu-plan` reads with `roles/viewer`, which also reads the objects of any bucket that still has Cloud Storage's default project-wide bindings (`legacyObjectReader` for `projectViewer`) — and this identity runs on every same-repo pull request. That is why module `pipeline`'s three media buckets replace those defaults with an authoritative IAM policy (ADR 0030); any bucket added later that holds private data needs the same, or a pull request can read it.
- By hand: creating the projects and linking billing, budgets, Neon, secret values, and the GitHub environments, variables and repository secret below.
- Cloud Run: this code owns each service's shape (scaling, CPU, memory, runtime identity, public or private); the deploy owns image, env vars, secret references and startup probe.
- `deploy.sh` is the deploy: one service, one image by digest, with its startup probe on `/health`. CI runs it (`.github/workflows/ci.yml`); `ci/` has the scripts CI uses to build and check images, to check a deploy, and to plan and apply OpenTofu.

| | staging | production |
| --- | --- | --- |
| `vision-api` | public, max 2 | public, max 5 |
| `vision-worker` | private, 2 vCPU, 2 GiB, max 1 | private, 2 vCPU, 2 GiB, max 3 |
| Deployer `github-deployer` | trusted from environment `staging` | trusted from environment `production` |
| Artifact Registry `vision` | pulls from production | `github-builder` pushes from `main` |
| Uptime check | `vision-api` `/health`, emails on failure | the same |
| Buckets `<project>-uploads`, `-clips`, `-output` | private, 7-day soft delete; originals deleted after 7 days; growth alert at 5 GiB each | the same; growth alert at 50 GiB each |
| Upload queue | `vision-uploads` → push to `vision-worker` as `vision-worker-invoker`; 5 attempts, then `vision-uploads-dead-letter` | the same |
| Queue alerts | dead letters, oldest message over 10 minutes, push failures over 20% for 15 minutes | the same |

## Media storage and the upload queue

Module `modules/pipeline`, used by both roots.

- **Buckets:** originals go to `<project>-uploads`, renditions to `<project>-clips`, analysis output to `<project>-output`. Object names start `tenant/<tenantId>/subject/<subjectId>/`, so deleting a tenant or a subject is a prefix delete; nothing in the bucket enforces the shape. Uniform access, public access prevention, and soft delete for 7 days: a deleted object can be restored for 7 days, and is stored and billed until then. A lifecycle rule deletes any original still in `uploads` after 7 days.
- **Who can read them:** each bucket has an authoritative IAM policy holding only the bindings in the module's `bucket_bindings` (today the worker's identity). A new bucket otherwise lets every project Viewer, Editor and Owner read its objects. A grant added by hand, or as a separate `_iam_member` resource, is removed by the next apply: add it to `bucket_bindings`. Check with `gcloud storage buckets get-iam-policy gs://<project>-uploads`.
- **Access for testing:** the variable `media_test_members` in staging's root, and `media_test_members_production` in production's, is a comma-separated list of people, each as `user:<address>` (the plan refuses anything else: a group, a domain, a service account or `allUsers` could let in more than the person meant), who get `roles/storage.objectUser` on the three buckets as part of the policy, so an apply keeps them. The addresses stay out of this repo: CI reads the repository secret `MEDIA_TEST_MEMBERS` for staging and `MEDIA_TEST_MEMBERS_PRODUCTION` for production, a local run reads `TF_VAR_media_test_members` in staging and `TF_VAR_media_test_members_production` in production: two names, so a value exported for staging is never read by production. For each root, CI and local must hold the same value, or the next apply from the other side undoes it. Empty or unset means nobody; to end the access, delete the secret and run the workflow on `main`. Production's buckets hold real users' videos: set its secret only for a test that needs it, and delete it afterwards.
- **Queue:** an object finished under `tenant/` in `uploads` is published to the topic `vision-uploads`; the subscription `vision-uploads-worker` pushes it to `vision-worker` at `/pubsub/uploads`, signed as `vision-worker-invoker`, the only identity allowed to call the worker. A message the worker doesn't accept is retried with a growing delay and after 5 attempts goes to `vision-uploads-dead-letter`, which keeps it for 7 days. Read them with `gcloud pubsub subscriptions pull vision-uploads-dead-letter --project=<project> --limit=10`.
- **No Eventarc:** an Eventarc trigger creates and manages its own subscription, and a dead-letter topic can only be put on it by hand afterwards. Built from its parts, the whole path is in this code.
- **Alerts** go to the same address as the uptime check. The metric names in them are checked by Cloud Monitoring on the first apply.

## Pipeline

`.github/workflows/ci.yml` plans and applies it when `infra/` or that workflow changes. It needs the repository secret `ALERT_EMAIL`, the alert address of both roots, and the variables `bootstrap.sh` prints when it ends: `TOFU_PLAN_WIF_PROVIDER_STAGING`, `TOFU_PLAN_SA_STAGING`, `TOFU_PLAN_WIF_PROVIDER_PRODUCTION` and `TOFU_PLAN_SA_PRODUCTION` on the repository, and `TOFU_WIF_PROVIDER` and `TOFU_SA` on each of the environments `infra-staging` and `infra-production`. A pull request that touches `infra/` fails `tofu-plan` until the secret and the four repository variables exist.

- **Pull request into `main`, same repository:** `tofu-plan` runs read-only for both roots and keeps one comment up to date, listing each resource's address and action, never a value. Pull requests from forks and from Dependabot get no OIDC token and skip it cleanly.
- **Merge to `main`:** `tofu-plan` runs after `deploy-staging`, so staging's plan sees the revision this merge just deployed, then saves a plan per root. A root with changes is applied from exactly that saved plan, staging in `infra-staging`, then production in `infra-production` after Ivan's approval.

The full plan, named in the comment or the run's summary, is never in a log or an artifact; read it locally from `infra/envs/<root>` after `tofu init`:

```bash
gcloud storage cp gs://<project>-tfstate/plans/<name>.tfplan /tmp/ && tofu show /tmp/<name>.tfplan
```

An apply refuses a plan that is out of date, either because the state has moved or because a service was deployed after a plan that changes it. Start a fresh plan with Actions → CI → Run workflow on `main`, which also redeploys staging.

## Working locally

Needs OpenTofu 1.13.9 and `gcloud auth application-default login`. Both roots read the alert address from the environment:

```bash
export TF_VAR_alert_email=<alert address>
```

Staging also reads `TF_VAR_media_test_members`, and production `TF_VAR_media_test_members_production` (see above). Set each to what that root's secret holds, and leave it unset only if that secret is unset too.

Staging first: production's registry grants name staging's deployer and Cloud Run service agent.

```bash
cd infra/envs/staging && tofu init && tofu plan -lock=false
```
