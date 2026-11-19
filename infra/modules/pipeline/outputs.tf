output "bucket_names" {
  description = "Name of each bucket, by role (uploads, clips, output)."
  value       = { for key, bucket in google_storage_bucket.this : key => bucket.name }
}

output "uploads_topic" {
  description = "Full id of the vision-uploads topic."
  value       = google_pubsub_topic.uploads.id
}

output "dead_letter_topic" {
  description = "Full id of the vision-uploads-dead-letter topic."
  value       = google_pubsub_topic.dead_letter.id
}

output "worker_subscription" {
  description = "Name of the push subscription that delivers to the worker."
  value       = google_pubsub_subscription.worker.name
}

output "invoker_sa_email" {
  description = "Email of the push identity (vision-worker-invoker)."
  value       = google_service_account.invoker.email
}
