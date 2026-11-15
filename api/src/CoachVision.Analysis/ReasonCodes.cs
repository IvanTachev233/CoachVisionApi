namespace CoachVision.Analysis;

/// <summary>Known `ReasonCode` values this engine produces (contract: `ReasonCode`). Named as
/// constants, not an enum, because the contract allows new values a client must treat as opaque,
/// and because they are serialised as these exact lower_snake_case strings.</summary>
public static class ReasonCodes
{
    public const string LiftNotSupported = "lift_not_supported";
    public const string PlatesNotFound = "plates_not_found";
    public const string PoseNotFound = "pose_not_found";
    public const string NoRepsDetected = "no_reps_detected";
    public const string LowConfidence = "low_confidence";

    /// <summary>The bar's own rep count disagreed with the shoulders' count: a wrong circle (the
    /// wrong size, or following something that isn't the bar) must not pass in silence, so the bar
    /// is not used for anything on this clip once this happens.</summary>
    public const string BarTrackMismatch = "bar_track_mismatch";
}
