# Media storage and the upload job queue: buckets, the Cloud Storage ->
# Pub/Sub -> worker path, and its alerts (V3a).
#
# Row V3a says "Eventarc", but an Eventarc trigger creates and manages its own
# Pub/Sub subscription (the provider documents transport.pubsub.subscription as
# output-only, "created and managed by Eventarc"), and a dead-letter topic is
# only ever added to that subscription by `gcloud pubsub subscriptions update`
# afterwards, outside OpenTofu. So the same path (storage event -> topic ->
# push subscription -> worker) is built from its parts below, all declared
# here, which lets the dead-letter topic be declared too.
#
# Object paths in all three buckets are "tenant/<tenantId>/subject/<subjectId>/...",
# so tenant and subject deletion are prefix deletes. Nothing in the bucket
# enforces this; the upload path's own filter (object_name_prefix below) is
# the only thing that depends on it.

locals {
  # readiness plan convention: "Labels on every resource: service, env".
  labels = {
    service = "vision"
    env     = var.environment
  }

  buckets = {
    uploads = {
      description = "Original uploads"
      lifecycle_rules = [
        { action_type = "Delete", age_days = 7 },
        { action_type = "AbortIncompleteMultipartUpload", age_days = 1 },
      ]
    }
    clips = {
      description     = "Renditions"
      lifecycle_rules = []
    }
    output = {
      description     = "Analysis output"
      lifecycle_rules = []
    }
  }

  # Deliberate exception to "additive IAM only" (LiftBig's ADR 0024),
  # recorded in ADR 0030. A uniform-access bucket is still created with
  # default project-wide bindings (legacyObjectReader/legacyBucketReader for
  # projectViewer; legacyObjectOwner/legacyBucketOwner for projectEditor and
  # projectOwner) that an additive _iam_member grant cannot remove, so every
  # project Viewer -- including the read-only plan identity
  # github-tofu-plan, usable from any same-repo pull request -- could read
  # users' videos. The authoritative policy below replaces those defaults on
  # the three media buckets with exactly the bindings here; after apply the
  # project's owners no longer read these objects by default: someone who
  # needs access for testing is named in var.test_members, which is part of
  # the policy, so an apply keeps it. V3b adds the API's bucket bindings as
  # another entry per bucket here, not as a separate _iam_member resource --
  # an _iam_member beside an authoritative policy would be removed on the
  # next apply.
  bucket_bindings = {
    for key in keys(local.buckets) : key => {
      # roles/storage.objectUser, not a narrower role: on uploads the worker
      # reads and deletes the original; on clips/output a retried job
      # overwrites its own earlier objects, and overwriting needs delete as
      # well as create.
      "roles/storage.objectUser" = [var.worker_runtime_sa_email]
    }
  }

  # The role var.test_members get on every media bucket: read, write and
  # delete objects, enough to upload a test clip and to look at what the
  # worker wrote. Bucket settings and the policy itself stay out of reach.
  test_role = "roles/storage.objectUser"
}

# --- Buckets ------------------------------------------------------------

resource "google_storage_bucket" "this" {
  for_each = local.buckets

  project                     = var.project_id
  name                        = "${var.project_id}-${each.key}"
  location                    = var.region
  uniform_bucket_level_access = true
  public_access_prevention    = "enforced"
  labels                      = local.labels

  # 7-day undelete window on every bucket (row V3a: "soft delete").
  soft_delete_policy {
    retention_duration_seconds = 604800
  }

  dynamic "lifecycle_rule" {
    for_each = each.value.lifecycle_rules
    content {
      action {
        type = lifecycle_rule.value.action_type
      }
      condition {
        age = lifecycle_rule.value.age_days
      }
    }
  }

  # No lifecycle rule on clips/output here: the readiness plan's "colder
  # storage after N months" has no N yet (open item, left for a later task).
}

# Authoritative on purpose (see local.bucket_bindings above): replaces the
# bucket's default bindings, leaving only the roles declared here. No grant
# for the API's identity in this task: V3b adds its own entry per bucket to
# local.bucket_bindings, inside this same policy.
data "google_iam_policy" "bucket" {
  for_each = local.bucket_bindings

  dynamic "binding" {
    for_each = each.value
    content {
      role = binding.key
      # People named for testing are part of this policy; a grant made by
      # hand beside it would be removed by the next apply.
      members = concat(
        [for member in binding.value : "serviceAccount:${member}"],
        binding.key == local.test_role ? var.test_members : [],
      )
    }
  }
}

resource "google_storage_bucket_iam_policy" "this" {
  for_each = google_storage_bucket.this

  bucket      = each.value.name
  policy_data = data.google_iam_policy.bucket[each.key].policy_data
}

# --- Queue: bucket notification -> topic -> push subscription -> worker ----

resource "google_pubsub_topic" "uploads" {
  project = var.project_id
  name    = "vision-uploads"
  labels  = local.labels
}

data "google_storage_project_service_account" "gcs" {
  project = var.project_id
}

resource "google_pubsub_topic_iam_member" "gcs_publishes_uploads" {
  project = var.project_id
  topic   = google_pubsub_topic.uploads.name
  role    = "roles/pubsub.publisher"
  member  = "serviceAccount:${data.google_storage_project_service_account.gcs.email_address}"
}

resource "google_storage_notification" "uploads" {
  bucket             = google_storage_bucket.this["uploads"].name
  topic              = google_pubsub_topic.uploads.id
  payload_format     = "JSON_API_V1"
  event_types        = ["OBJECT_FINALIZE"]
  object_name_prefix = "tenant/"

  depends_on = [google_pubsub_topic_iam_member.gcs_publishes_uploads]
}

# A dedicated identity that only invokes the worker; nothing else is granted
# to it.
resource "google_service_account" "invoker" {
  project      = var.project_id
  account_id   = "vision-worker-invoker"
  display_name = "vision-worker-invoker"
  description  = "Invokes the vision-worker Cloud Run service from its Pub/Sub push subscription; no other permissions"
}

# Module `service` grants run.invoker itself only when `public = true`
# (google_cloud_run_v2_service_iam_member.public, count = var.public ? 1 : 0);
# the worker is private, so that resource has no instance (count = 0) and
# this non-authoritative member binding cannot clash with it.
resource "google_cloud_run_v2_service_iam_member" "worker_invoker" {
  project  = var.project_id
  location = var.region
  name     = var.worker_service_name
  role     = "roles/run.invoker"
  member   = google_service_account.invoker.member
}

# Suggested path for V3c's Pub/Sub push route; until that ships the hello
# worker answers 404 there, which is how the DoD's poison-message check runs.
resource "google_pubsub_subscription" "worker" {
  project = var.project_id
  name    = "vision-uploads-worker"
  topic   = google_pubsub_topic.uploads.id

  # The maximum: analysis takes up to 4.5x a clip's length, clips are at
  # most 60s.
  ack_deadline_seconds       = 600
  message_retention_duration = "604800s"
  labels                     = local.labels

  expiration_policy {
    ttl = "" # never expires
  }

  retry_policy {
    minimum_backoff = "10s"
    maximum_backoff = "600s"
  }

  push_config {
    push_endpoint = "${var.worker_uri}/pubsub/uploads"

    oidc_token {
      service_account_email = google_service_account.invoker.email
      audience              = var.worker_uri
    }
  }

  dead_letter_policy {
    dead_letter_topic     = google_pubsub_topic.dead_letter.id
    max_delivery_attempts = 5
  }

  # The push identity must be able to invoke the worker, and Pub/Sub must be
  # able to publish to the dead-letter topic, before the subscription exists.
  depends_on = [
    google_cloud_run_v2_service_iam_member.worker_invoker,
    google_pubsub_topic_iam_member.dead_letter_publisher,
  ]
}

# --- Dead letters ---------------------------------------------------------
# A topic with no subscription loses whatever is published to it.

resource "google_pubsub_topic" "dead_letter" {
  project = var.project_id
  name    = "vision-uploads-dead-letter"
  labels  = local.labels
}

resource "google_pubsub_subscription" "dead_letter" {
  project = var.project_id
  name    = "vision-uploads-dead-letter"
  topic   = google_pubsub_topic.dead_letter.id

  message_retention_duration = "604800s"
  labels                     = local.labels

  expiration_policy {
    ttl = "" # never expires
  }
}

data "google_project" "this" {
  project_id = var.project_id
}

resource "google_pubsub_topic_iam_member" "dead_letter_publisher" {
  project = var.project_id
  topic   = google_pubsub_topic.dead_letter.name
  role    = "roles/pubsub.publisher"
  member  = "serviceAccount:service-${data.google_project.this.number}@gcp-sa-pubsub.iam.gserviceaccount.com"
}

resource "google_pubsub_subscription_iam_member" "dead_letter_forwarder" {
  project      = var.project_id
  subscription = google_pubsub_subscription.worker.name
  role         = "roles/pubsub.subscriber"
  member       = "serviceAccount:service-${data.google_project.this.number}@gcp-sa-pubsub.iam.gserviceaccount.com"
}

# --- Alerts ----------------------------------------------------------------
# module.monitoring (LiftBig tag infra-modules-v2) has no output for the
# channel it creates; its own threshold_alerts/log_alerts inputs also have no
# denominator_filter/denominator_aggregations, which the failure-rate alert
# below needs. So every alert here is a plain google_monitoring_alert_policy
# that looks the channel up by the display name each root already passes to
# module.monitoring, instead of mixing two mechanisms. Callers must
# depends_on = [module.monitoring] on this module.

data "google_monitoring_notification_channel" "email" {
  project      = var.project_id
  display_name = var.channel_display_name
}

resource "google_monitoring_alert_policy" "dead_letter" {
  project               = var.project_id
  display_name          = "Vision ${var.project_id}: dead-letter messages"
  combiner              = "OR"
  notification_channels = [data.google_monitoring_notification_channel.email.id]
  user_labels           = local.labels
  documentation {
    content = "A message reached vision-uploads-dead-letter: the worker rejected or never acked an upload notification 5 times. Check the worker's logs for the failing message, then re-publish it once fixed."
  }

  conditions {
    display_name = "dead_letter_message_count > 0"

    condition_threshold {
      # unverified until first apply (metric type checked by Monitoring at create)
      filter          = "resource.type=\"pubsub_subscription\" AND resource.label.subscription_id=\"${google_pubsub_subscription.worker.name}\" AND metric.type=\"pubsub.googleapis.com/subscription/dead_letter_message_count\""
      comparison      = "COMPARISON_GT"
      threshold_value = 0
      duration        = "0s" # any message in the aggregation window alerts

      aggregations {
        alignment_period   = "300s" # row V3a: "any message in 5 minutes"
        per_series_aligner = "ALIGN_SUM"
      }
    }
  }
}

resource "google_monitoring_alert_policy" "oldest_unacked" {
  project               = var.project_id
  display_name          = "Vision ${var.project_id}: oldest unacked message > 10 min"
  combiner              = "OR"
  notification_channels = [data.google_monitoring_notification_channel.email.id]
  user_labels           = local.labels
  documentation {
    content = "The oldest unacked message on vision-uploads-worker is over 10 minutes old: the worker is failing, stuck, or has no capacity. Check vision-worker's logs and revision health."
  }

  conditions {
    display_name = "oldest_unacked_message_age > 600s"

    condition_threshold {
      # unverified until first apply (metric type checked by Monitoring at create)
      filter          = "resource.type=\"pubsub_subscription\" AND resource.label.subscription_id=\"${google_pubsub_subscription.worker.name}\" AND metric.type=\"pubsub.googleapis.com/subscription/oldest_unacked_message_age\""
      comparison      = "COMPARISON_GT"
      threshold_value = 600
      duration        = "0s"

      aggregations {
        alignment_period   = "300s"
        per_series_aligner = "ALIGN_MAX" # gauge: use the worst value seen in the window
      }
    }
  }
}

resource "google_monitoring_alert_policy" "failure_rate" {
  project               = var.project_id
  display_name          = "Vision ${var.project_id}: push failure rate > 20%"
  combiner              = "OR"
  notification_channels = [data.google_monitoring_notification_channel.email.id]
  user_labels           = local.labels
  documentation {
    content = "More than 20% of pushes to vision-worker over 15 minutes are not acked. Check vision-worker's logs and the worker's push endpoint for errors."
  }

  conditions {
    display_name = "non-ack push_request_count ratio > 0.2"

    # assumption: Ivan to confirm (threshold and duration)
    condition_threshold {
      # unverified until first apply (metric type checked by Monitoring at create)
      filter          = "resource.type=\"pubsub_subscription\" AND resource.label.subscription_id=\"${google_pubsub_subscription.worker.name}\" AND metric.type=\"pubsub.googleapis.com/subscription/push_request_count\" AND metric.label.response_class!=\"ack\""
      comparison      = "COMPARISON_GT"
      threshold_value = 0.2
      duration        = "900s"

      # push_request_count has several series per subscription (response_class,
      # response_code, delivery_type, ...); both sides of the ratio must be
      # reduced to one series per subscription or the ratio is meaningless.
      aggregations {
        alignment_period     = "300s"
        per_series_aligner   = "ALIGN_SUM"
        cross_series_reducer = "REDUCE_SUM"
        group_by_fields      = ["resource.label.subscription_id"]
      }

      denominator_filter = "resource.type=\"pubsub_subscription\" AND resource.label.subscription_id=\"${google_pubsub_subscription.worker.name}\" AND metric.type=\"pubsub.googleapis.com/subscription/push_request_count\""

      denominator_aggregations {
        alignment_period     = "300s"
        per_series_aligner   = "ALIGN_SUM"
        cross_series_reducer = "REDUCE_SUM"
        group_by_fields      = ["resource.label.subscription_id"]
      }
    }
  }
}

resource "google_monitoring_alert_policy" "bucket_growth" {
  for_each = google_storage_bucket.this

  project               = var.project_id
  display_name          = "Vision ${var.project_id}: ${each.key} bucket over threshold"
  combiner              = "OR"
  notification_channels = [data.google_monitoring_notification_channel.email.id]
  user_labels           = local.labels
  documentation {
    content = "Bucket ${each.value.name} has grown past its threshold. Check whether the uploads lifecycle rule is running and whether usage genuinely grew."
  }

  conditions {
    display_name = "total_bytes > threshold"

    # assumption: Ivan to confirm (per-env threshold)
    condition_threshold {
      # unverified until first apply (metric type checked by Monitoring at create)
      filter          = "resource.type=\"gcs_bucket\" AND resource.label.bucket_name=\"${each.value.name}\" AND metric.type=\"storage.googleapis.com/storage/v2/total_bytes\""
      comparison      = "COMPARISON_GT"
      threshold_value = var.bucket_growth_alert_bytes
      duration        = "0s"

      aggregations {
        # Written about once a day: align over a full day and take the
        # latest/largest point seen in it, so a day with no new point still
        # evaluates against the last known size. total_bytes is one series
        # per storage class and per object type (live, soft-deleted, ...);
        # reduce to the bucket's total so this compares the whole bucket.
        # Soft-deleted bytes count too: they are still stored and billed.
        alignment_period     = "86400s"
        per_series_aligner   = "ALIGN_MAX"
        cross_series_reducer = "REDUCE_SUM"
        group_by_fields      = ["resource.label.bucket_name"]
      }
    }
  }
}
