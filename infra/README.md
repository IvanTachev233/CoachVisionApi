# Infrastructure

Google Cloud for Vision, managed with OpenTofu. Two projects, `coachvision-staging` and `coachvision-prod`, each a root in `envs/` with its state in that project's `<project>-tfstate` bucket. The modules are LiftBig's (`service`, `github-ci`, `monitoring`), read from the public LiftBig repo at a tag (`ref` in each root's `locals`); move both roots to a new tag together.

This repository is public: no secrets, personal email addresses or billing ids in code, and no plan output in CI logs.

- `bootstrap.sh` makes what OpenTofu needs before it can run: the APIs the first plan calls and the state bucket. It stays outside OpenTofu so an apply can't lock itself out.
- By hand: creating the projects and linking billing, budgets, Neon, secret values, GitHub environments and variables.
- Cloud Run: this code owns each service's shape (scaling, CPU, memory, runtime identity, public or private); the deploy owns image, env vars, secret references and startup probe.
- `deploy.sh` is the deploy: one service, one image by digest, with its startup probe on `/health`. CI runs it (`.github/workflows/ci.yml`); `ci/` has the scripts CI uses to build and check images and to check a deploy.

| | staging | production |
| --- | --- | --- |
| `vision-api` | public, max 2 | public, max 5 |
| `vision-worker` | private, 2 vCPU, 2 GiB, max 1 | private, 2 vCPU, 2 GiB, max 3 |
| Deployer `github-deployer` | trusted from environment `staging` | trusted from environment `production` |
| Artifact Registry `vision` | pulls from production | `github-builder` pushes from `main` |
| Uptime check | `vision-api` `/health`, emails on failure | the same |

## Working locally

Applies run locally for now. Needs OpenTofu 1.13.9 and `gcloud auth application-default login`. Both roots read the alert address from the environment:

```bash
export TF_VAR_alert_email=<alert address>
```

Staging first: production's registry grants name staging's deployer and Cloud Run service agent.

```bash
cd infra/envs/staging && tofu init && tofu plan
```
