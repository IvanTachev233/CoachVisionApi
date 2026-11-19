# Vision production: the API and worker services, GitHub Actions access,
# monitoring, and the image registry staging also pulls from (build once,
# promote by digest, as LiftBig's ADR 0023). Apply staging first: the registry
# grants name staging's deployer and Cloud Run service agent.

locals {
  project_id        = "coachvision-prod"
  region            = "europe-west1"
  github_repo       = "IvanTachev233/CoachVisionApi"
  github_repo_id    = "1457436014"
  github_sub_prefix = "repo:IvanTachev233@63864892/CoachVisionApi@1457436014"

  # LiftBig's modules at a tag (ADR 0017 addendum); move both roots to a new tag together.
  modules = "git::https://github.com/IvanTachev233/LiftBig.git//infra/modules"
  ref     = "?ref=infra-modules-v2"

  staging_project_id = "coachvision-staging"

  apis = toset([
    "artifactregistry.googleapis.com",
    "cloudresourcemanager.googleapis.com",
    "iam.googleapis.com",
    "iamcredentials.googleapis.com",
    "monitoring.googleapis.com",
    "pubsub.googleapis.com",
    "run.googleapis.com",
    "secretmanager.googleapis.com",
    "serviceusage.googleapis.com",
    "storage.googleapis.com",
    "sts.googleapis.com",
  ])
}

data "google_project" "this" {
  project_id = local.project_id
}

data "google_project" "staging" {
  project_id = local.staging_project_id
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
  secrets                 = ["vision-db-connection-string"] # V4a; the runbook has Ivan put a value in once the role exists
  max_instances           = 5

  depends_on = [google_project_service.this]
}

# Private worker: only identities granted run.invoker elsewhere can call it.
# max_instances becomes the Cloud Run scaling limit via module `service`'s
# `scaling { max_instance_count = var.max_instances }` (infra-modules-v2
# infra/modules/service/main.tf): the cap row V3a asks for is already in
# place here, capped lower than the API's.
module "worker" {
  source = "${local.modules}/service${local.ref}"

  project_id              = local.project_id
  region                  = local.region
  name                    = "vision-worker"
  runtime_sa_id           = "vision-worker-runtime"
  runtime_sa_display_name = "vision-worker-runtime"
  secrets                 = []
  max_instances           = 3
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
  github_environment    = "production"
  deployer_description  = "Deploys vision-api and vision-worker (production) from GitHub Actions"
  runtime_sa_name       = module.api.runtime_sa_name
}

# The module covers one runtime identity; the deployer attaches the worker's too.
resource "google_service_account_iam_member" "deployer_acts_as_worker" {
  service_account_id = module.worker.runtime_sa_name
  role               = "roles/iam.serviceAccountUser"
  member             = module.github_ci.deployer_member
}

# --- Registry and the identity that builds images ---------------------------

resource "google_artifact_registry_repository" "vision" {
  project                = local.project_id
  location               = local.region
  repository_id          = "vision"
  format                 = "DOCKER"
  description            = "Vision images, built once on main and promoted by digest"
  cleanup_policy_dry_run = false

  cleanup_policies {
    id     = "delete-old"
    action = "DELETE"

    condition {
      tag_state  = "ANY"
      older_than = "2592000s"
    }
  }

  cleanup_policies {
    id     = "keep-newest"
    action = "KEEP"

    most_recent_versions {
      keep_count = 30
    }
  }

  depends_on = [google_project_service.this]
}

resource "google_service_account" "builder" {
  project      = local.project_id
  account_id   = "github-builder"
  display_name = "github-builder"
  description  = "Pushes Vision images from main to the vision registry; can't deploy"
}

resource "google_service_account_iam_member" "builder_from_github" {
  service_account_id = google_service_account.builder.name
  role               = "roles/iam.workloadIdentityUser"
  member             = "principal://iam.googleapis.com/${module.github_ci.pool_name}/subject/${local.github_sub_prefix}:ref:refs/heads/main"
}

resource "google_artifact_registry_repository_iam_member" "writer" {
  project    = local.project_id
  location   = local.region
  repository = google_artifact_registry_repository.vision.repository_id
  role       = "roles/artifactregistry.writer"
  member     = google_service_account.builder.member
}

resource "google_artifact_registry_repository_iam_member" "reader" {
  for_each = {
    production_deployer = module.github_ci.deployer_member
    staging_deployer    = "serviceAccount:github-deployer@${local.staging_project_id}.iam.gserviceaccount.com"
    production_agent    = "serviceAccount:service-${data.google_project.this.number}@serverless-robot-prod.iam.gserviceaccount.com"
    staging_agent       = "serviceAccount:service-${data.google_project.staging.number}@serverless-robot-prod.iam.gserviceaccount.com"
  }

  project    = local.project_id
  location   = local.region
  repository = google_artifact_registry_repository.vision.repository_id
  role       = "roles/artifactregistry.reader"
  member     = each.value

  # The service agent appears once Cloud Run is in use in the project.
  depends_on = [module.api, module.worker]
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
  channel_display_name = "Vision production"
  alert_email          = var.alert_email

  uptime = {
    display_name       = "Vision production API up"
    host               = trimprefix(module.api.uri, "https://")
    path               = "/health"
    alert_display_name = "Vision production API uptime failure"
    notify             = true
  }
}

# --- Media storage and the upload job queue (V3a) ---------------------------

# Who may read and write the media buckets for testing, besides the worker.
# The buckets' policy is authoritative (ADR 0030), so access given by hand is
# removed by the next apply; a tester is named here instead. These buckets
# hold real users' videos: keep the list empty unless a test needs it, and
# empty it again afterwards. Its own name and its own secret in CI, so that a
# value set for staging (TF_VAR_media_test_members) is never read here.
variable "media_test_members_production" {
  description = "People, each as user:<address>, separated by commas or new lines, given read and write on the three media buckets for testing. Nothing else is accepted. Kept out of the repo; set TF_VAR_media_test_members_production (CI: the repository secret MEDIA_TEST_MEMBERS_PRODUCTION). Empty: nobody."
  type        = string
  default     = ""
  sensitive   = true
}

module "pipeline" {
  source = "../../modules/pipeline"

  project_id              = local.project_id
  region                  = local.region
  worker_uri              = module.worker.uri
  worker_service_name     = "vision-worker"
  worker_runtime_sa_email = module.worker.runtime_sa_email
  channel_display_name    = "Vision production"
  environment             = "production"
  test_members            = compact([for member in split(",", replace(var.media_test_members_production, "\n", ",")) : trimspace(member)])
  # assumption: Ivan to confirm
  bucket_growth_alert_bytes = 50 * 1024 * 1024 * 1024 # 50 GiB

  # module.worker: module `service` at infra-modules-v2 has no name output,
  # so nothing else orders the worker's existence before the invoker grant.
  depends_on = [google_project_service.this, module.monitoring, module.worker]
}
