using System.Text.Json.Serialization;

namespace CoachVision.Analysis;

/// <summary>Shared shape of "could not be analysed" (contract: `Unanalysable`): every feature's
/// `oneOf` has its own concrete type (so each stays in its own `JsonDerivedType` hierarchy), but
/// they all carry the same three members, reachable through this interface without a cast.</summary>
public interface IUnanalysable
{
    string Reason { get; }
    string? Detail { get; }
    string ModelVersion { get; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "outcome")]
[JsonDerivedType(typeof(BarPathAnalysed), "analysed")]
[JsonDerivedType(typeof(BarPathUnanalysable), "unanalysable")]
public abstract record BarPathResult;

/// <summary>The bar's centre over time, metres from its position in the first sample
/// (contract: `BarPathAnalysed`).</summary>
public sealed record BarPathAnalysed : BarPathResult
{
    public required double Confidence { get; init; }
    public required string ModelVersion { get; init; }
    public required IReadOnlyList<BarPathSample> Samples { get; init; }
}

public sealed record BarPathUnanalysable : BarPathResult, IUnanalysable
{
    public required string Reason { get; init; }

    // The null suppression lives on the type, not on a caller's JsonSerializerOptions: the
    // contract types `detail` as a plain string when present, never a JSON null.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }

    public required string ModelVersion { get; init; }
}

public sealed record BarPathSample
{
    public required double T { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Px { get; init; }
    public required double Py { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "outcome")]
[JsonDerivedType(typeof(VelocityAnalysed), "analysed")]
[JsonDerivedType(typeof(VelocityUnanalysable), "unanalysable")]
public abstract record VelocityResult;

public sealed record VelocityAnalysed : VelocityResult
{
    public required double Confidence { get; init; }
    public required string ModelVersion { get; init; }
    public required IReadOnlyList<RepVelocity> Reps { get; init; }
}

public sealed record VelocityUnanalysable : VelocityResult, IUnanalysable
{
    public required string Reason { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }

    public required string ModelVersion { get; init; }
}

public sealed record RepVelocity
{
    public required int Rep { get; init; }
    public required double MeanConcentricMps { get; init; }
    public required double PeakConcentricMps { get; init; }

    /// <summary>The mean of the bar detector's own per-frame confidence over the frames this
    /// rep's speed was measured from. Always set by the engine (never left to serialise as
    /// null); the attribute stays as a defence against a future code path that forgets to.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Confidence { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "outcome")]
[JsonDerivedType(typeof(RepCountAnalysed), "analysed")]
[JsonDerivedType(typeof(RepCountUnanalysable), "unanalysable")]
public abstract record RepCountResult;

public sealed record RepCountAnalysed : RepCountResult
{
    public required double Confidence { get; init; }
    public required string ModelVersion { get; init; }
    public required int Count { get; init; }
    public required IReadOnlyList<RepTiming> Reps { get; init; }
}

public sealed record RepCountUnanalysable : RepCountResult, IUnanalysable
{
    public required string Reason { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }

    public required string ModelVersion { get; init; }
}

public sealed record RepTiming
{
    public required int Rep { get; init; }
    public required double StartS { get; init; }
    public required double BottomS { get; init; }
    public required double EndS { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "outcome")]
[JsonDerivedType(typeof(SquatDepthAnalysed), "analysed")]
[JsonDerivedType(typeof(SquatDepthUnanalysable), "unanalysable")]
public abstract record SquatDepthResult;

public sealed record SquatDepthAnalysed : SquatDepthResult
{
    public required double Confidence { get; init; }
    public required string ModelVersion { get; init; }
    public required bool AllRepsPassed { get; init; }
    public required IReadOnlyList<RepDepth> Reps { get; init; }
}

public sealed record SquatDepthUnanalysable : SquatDepthResult, IUnanalysable
{
    public required string Reason { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }

    public required string ModelVersion { get; init; }
}

public sealed record RepDepth
{
    public required int Rep { get; init; }
    public required bool Passed { get; init; }
    public required double MinKneeAngleDeg { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Confidence { get; init; }
}

/// <summary>The checks that exist for the lift, keyed by check (contract: `VerificationChecksResult`).
/// In v1 there is one, `squat_depth`.</summary>
public sealed record VerificationChecksResult
{
    [JsonPropertyName("squat_depth")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SquatDepthResult? SquatDepth { get; init; }
}

/// <summary>One result per requested feature, plus the result format version every job carries
/// (contract: `Job.schemaVersion`, `Results`). Keys are the contract's snake_case feature names.
/// A feature not requested is left out of the JSON entirely (null-suppressed), not sent as a
/// null the contract's `Results` never defines.</summary>
public sealed record AnalysisResults
{
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("bar_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BarPathResult? BarPath { get; init; }

    [JsonPropertyName("velocity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VelocityResult? Velocity { get; init; }

    [JsonPropertyName("rep_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RepCountResult? RepCount { get; init; }

    [JsonPropertyName("verification_checks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VerificationChecksResult? VerificationChecks { get; init; }
}
