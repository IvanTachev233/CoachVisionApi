using Microsoft.EntityFrameworkCore;

namespace CoachVision.Data.Security;

/// <summary>Why a prospective job was or wasn't admitted, mapped by the caller to the contract's exact error.</summary>
public enum JobAdmissionOutcome
{
    Admitted,
    ClipTooLarge,
    ConcurrentJobsLimitReached,
    DailyMinutesLimitReached,
}

/// <summary>
/// <paramref name="RetryAfterSeconds"/> is set only for the two quota outcomes; concurrent-jobs is
/// rate-limited the same way the contract's other quotas are, but a job finishing (not a fixed
/// window) is what frees a slot, so this reports a conservative estimate rather than a real time.
/// <paramref name="LimitBytes"/> is set only for <see cref="JobAdmissionOutcome.ClipTooLarge"/>.
/// </summary>
public sealed record JobAdmissionResult(JobAdmissionOutcome Outcome, long? LimitBytes = null, int? RetryAfterSeconds = null)
{
    public static readonly JobAdmissionResult Admitted = new(JobAdmissionOutcome.Admitted);
}

/// <summary>
/// Three limits — concurrent jobs, daily minutes and clip size — checked from the database under
/// the tenant's own row-security scope. Rate limiting is a separate, request-level concern (the
/// API's rate limiter middleware), not part of job admission.
///
/// The caller must run this check and the job insert in one transaction, under one tenant scope:
/// two simultaneous create-job calls that each read "4 of 5 concurrent jobs used" before either
/// inserts would otherwise both be admitted, going over the limit. A row lock that serialises per
/// tenant (for example a <c>SELECT ... FOR UPDATE</c> on the tenant's own <c>tenant_limits</c> row,
/// taken before the count) closes that race, the same way the rotation trigger on <c>api_keys</c>
/// closes the equivalent one for issuing a key.
/// </summary>
public static class JobAdmission
{
    /// <summary>A job this far along no longer holds a concurrency slot; any other status does (including one not yet known of, by default).</summary>
    private static readonly string[] TerminalJobStatuses = ["completed", "failed"];

    public static async Task<JobAdmissionResult> CheckAsync(
        VisionDbContext db,
        Guid tenantId,
        long declaredSizeBytes,
        CancellationToken cancellationToken = default)
    {
        var limits = await TenantLimitsLookup.GetOrDefaultAsync(db, tenantId, cancellationToken);

        if (declaredSizeBytes > limits.MaxClipSizeBytes)
        {
            return new JobAdmissionResult(JobAdmissionOutcome.ClipTooLarge, LimitBytes: limits.MaxClipSizeBytes);
        }

        var openJobs = await db.Jobs.CountAsync(
            j => j.TenantId == tenantId && !TerminalJobStatuses.Contains(j.Status),
            cancellationToken);
        if (openJobs >= limits.ConcurrentJobsLimit)
        {
            return new JobAdmissionResult(JobAdmissionOutcome.ConcurrentJobsLimitReached, RetryAfterSeconds: 30);
        }

        var todayStartUtc = DateTime.UtcNow.Date;
        var usedSecondsToday = await db.Usage
            .Where(u => u.TenantId == tenantId && u.OccurredAtUtc >= todayStartUtc)
            .SumAsync(u => (decimal?)u.VideoSeconds, cancellationToken) ?? 0m;
        var dailyLimitSeconds = limits.DailyMinutesLimit * 60m;
        if (usedSecondsToday >= dailyLimitSeconds)
        {
            var secondsToNextUtcDay = (int)Math.Ceiling((todayStartUtc.AddDays(1) - DateTime.UtcNow).TotalSeconds);
            return new JobAdmissionResult(JobAdmissionOutcome.DailyMinutesLimitReached, RetryAfterSeconds: Math.Max(secondsToNextUtcDay, 1));
        }

        return JobAdmissionResult.Admitted;
    }
}

/// <summary>The outcome of checking an uploaded clip's measured length against its tenant's limit.</summary>
public enum ClipLengthOutcome
{
    Accepted,
    TooLong,
}

/// <summary>
/// Present only when <see cref="Outcome"/> is <see cref="ClipLengthOutcome.TooLong"/>, shaped to
/// match the contract's <c>Failure</c> schema fields directly (<c>reason</c>, <c>retryable</c>,
/// <c>detail</c>), so a caller can serialise it onto a failed job without translation.
/// </summary>
public sealed record ClipLengthFailure(string Reason, bool Retryable, string Detail);

public sealed record ClipLengthCheckResult(ClipLengthOutcome Outcome, ClipLengthFailure? Failure = null)
{
    public static readonly ClipLengthCheckResult Accepted = new(ClipLengthOutcome.Accepted);
}

/// <summary>
/// Whether an uploaded clip's measured duration is within its tenant's limit. Pure (no database
/// access): the duration is only known once the worker has read it, after upload, so this is
/// meant to be called from that completion path, not from job creation.
/// </summary>
public static class ClipLength
{
    public static ClipLengthCheckResult Check(TimeSpan measuredDuration, int maxClipLengthSeconds)
    {
        if (measuredDuration.TotalSeconds <= maxClipLengthSeconds)
        {
            return ClipLengthCheckResult.Accepted;
        }

        return new ClipLengthCheckResult(
            ClipLengthOutcome.TooLong,
            new ClipLengthFailure("video_too_long", Retryable: false, $"The limit is {maxClipLengthSeconds} seconds."));
    }
}
