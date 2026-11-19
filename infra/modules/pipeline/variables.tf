variable "project_id" {
  description = "Google Cloud project."
  type        = string
}

variable "region" {
  description = "Cloud Run / bucket region."
  type        = string
}

variable "worker_uri" {
  description = "The worker service's run.app URL (module.worker.uri in the root)."
  type        = string
}

variable "worker_service_name" {
  description = "The worker Cloud Run service's name, as passed to module.worker (e.g. \"vision-worker\")."
  type        = string
}

variable "worker_runtime_sa_email" {
  description = "Email of the worker's runtime identity (module.worker.runtime_sa_email), granted access to the buckets."
  type        = string
}

variable "channel_display_name" {
  description = "Display name of the email notification channel module.monitoring creates in this project (var.channel_display_name passed to module.monitoring)."
  type        = string
}

variable "test_members" {
  description = "People (\"user:<address>\") given roles/storage.objectUser on the three media buckets, for testing. Part of the authoritative policy, so an apply keeps them. Empty: nobody but the identities the module names."
  type        = list(string)
  default     = []
  # An address: kept out of plan output whatever the caller passes.
  sensitive = true

  # Named people only: never allUsers, allAuthenticatedUsers, a domain or a
  # group, each of which could let in more than the one person meant.
  validation {
    condition     = alltrue([for member in var.test_members : can(regex("^user:[^@\\s,:<>\"']+@[^@\\s,:<>\"']+$", member))])
    error_message = "Each test member must be exactly user:name@domain: lower-case \"user:\", no space after the colon, no quotes or brackets, several separated by commas or new lines. Groups, domains, service accounts and allUsers are refused."
  }
}

variable "bucket_growth_alert_bytes" {
  description = "Per-bucket total size that triggers the bucket growth alert, in bytes. # assumption: Ivan to confirm"
  type        = number
}

variable "environment" {
  description = "Label value for env: \"staging\" or \"production\"."
  type        = string
}
