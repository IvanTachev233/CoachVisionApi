# Vision staging: the API and worker services, GitHub Actions access and
# monitoring. Images come from production's registry (envs/production).

locals {
  project_id        = "coachvision-staging"
  region            = "europe-west1"
  github_repo       = "IvanTachev233/CoachVisionApi"
  github_repo_id    = "1457436014"
  github_sub_prefix = "repo:IvanTachev233@63864892/CoachVisionApi@1457436014"

  # LiftBig's modules at a tag (ADR 0017 addendum); move both roots to a new tag together.
  modules = "git::https://github.com/IvanTachev233/LiftBig.git//infra/modules"
  ref     = "?ref=infra-modules-v2"

  apis = toset([
    "cloudresourcemanager.googleapis.com",
    "iam.googleapis.com",
    "iamcredentials.googleapis.com",
    "monitoring.googleapis.com",
    "run.googleapis.com",
    "secretmanager.googleapis.com",
    "serviceusage.googleapis.com",
    "storage.googleapis.com",
    "sts.googleapis.com",
  ])
}

resource "google_project_service" "this" {
  for_each = local.apis

  project = local.project_id
  service = each.value
}

# Public API, on its run.app URL until Vision has a domain (ADR 0016).
module "api" {
  source = "${local.modules}/service${local.ref}"

  project_id              = local.project_id
  region                  = local.region
  name                    = "vision-api"
  runtime_sa_id           = "vision-api-runtime"
  runtime_sa_display_name = "vision-api-runtime"
  secrets                 = [] # the database secret comes with V4a, named as its code reads it
  max_instances           = 2

  depends_on = [google_project_service.this]
}

# Private worker: only identities granted run.invoker elsewhere can call it.
module "worker" {
  source = "${local.modules}/service${local.ref}"

  project_id              = local.project_id
  region                  = local.region
  name                    = "vision-worker"
  runtime_sa_id           = "vision-worker-runtime"
  runtime_sa_display_name = "vision-worker-runtime"
  secrets                 = []
  max_instances           = 1
  public                  = false
  cpu                     = "2"
  memory                  = "2Gi"

  depends_on = [google_project_service.this]
}

module "github_ci" {
  source = "${local.modules}/github-ci${local.ref}"

  project_id            = local.project_id
  pool_display_name     = "GitHub Actions"
  provider_id           = "github-actions"
  provider_display_name = "GitHub CoachVisionApi"
  github_repo           = local.github_repo
  github_repo_id        = local.github_repo_id
  github_sub_prefix     = local.github_sub_prefix
  github_environment    = "staging"
  deployer_description  = "Deploys vision-api and vision-worker (staging) from GitHub Actions"
  runtime_sa_name       = module.api.runtime_sa_name
}

# The module covers one runtime identity; the deployer attaches the worker's too.
resource "google_service_account_iam_member" "deployer_acts_as_worker" {
  service_account_id = module.worker.runtime_sa_name
  role               = "roles/iam.serviceAccountUser"
  member             = module.github_ci.deployer_member
}

# --- Monitoring -------------------------------------------------------------

variable "alert_email" {
  description = "Address alerts go to. Kept out of the repo; set TF_VAR_alert_email."
  type        = string
  sensitive   = true
}

module "monitoring" {
  source = "${local.modules}/monitoring${local.ref}"

  project_id           = local.project_id
  channel_display_name = "Vision staging"
  alert_email          = var.alert_email

  uptime = {
    display_name = "Vision staging API up"
    host         = trimprefix(module.api.uri, "https://")
    # Keep /health off the database, or a check every 5 minutes stops Neon's
    # Free plan from scaling to zero (as in LiftBig staging).
    path               = "/health"
    alert_display_name = "Vision staging API uptime failure"
    notify             = true
  }
}
