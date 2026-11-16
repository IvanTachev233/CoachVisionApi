using System.Text.Json.Serialization;

namespace CoachVision.Analysis;

/// <summary>What the worker wrote for one video (contracts/internal/raw-output/v1.json): per-frame
/// detections in pixels and nothing else. Deserialised from the worker's JSON, never produced here.</summary>
public sealed class RawOutputDocument
{
    public required string SchemaVersion { get; init; }
    public required string JobId { get; init; }
    public required RawWorker Worker { get; init; }
    public required RawVideo Video { get; init; }
    public required IReadOnlyList<RawFrame> Frames { get; init; }
}

/// <summary>What produced the raw output, so the model version can change when either changes.</summary>
public sealed class RawWorker
{
    public required string Version { get; init; }
    public required string PoseModel { get; init; }
    public required string BarDetector { get; init; }
}

public sealed class RawVideo
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double FrameRate { get; init; }
    public required double DurationS { get; init; }
    public required int FrameCount { get; init; }
}

public sealed class RawFrame
{
    public required int Index { get; init; }
    public required double T { get; init; }
    public RawBar? Bar { get; init; }
    public IReadOnlyDictionary<string, RawLandmark>? Pose { get; init; }
}

/// <summary>A detected plate, seen as the bar's end. <see cref="RadiusPx"/> with the job's declared
/// plate diameter gives the scale (ADR 0017, choice 3A).</summary>
public sealed class RawBar
{
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double RadiusPx { get; init; }
    public required double Confidence { get; init; }
}

public sealed class RawLandmark
{
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Confidence { get; init; }
}

/// <summary>Body point names the raw output may carry (contract: raw-output/v1.json, `pose`).
/// A `const` set because the contract names a fixed set of 2D landmarks (ADR 0017: never `z`).</summary>
public static class LandmarkNames
{
    public const string Nose = "nose";
    public const string LeftShoulder = "left_shoulder";
    public const string RightShoulder = "right_shoulder";
    public const string LeftElbow = "left_elbow";
    public const string RightElbow = "right_elbow";
    public const string LeftWrist = "left_wrist";
    public const string RightWrist = "right_wrist";
    public const string LeftHip = "left_hip";
    public const string RightHip = "right_hip";
    public const string LeftKnee = "left_knee";
    public const string RightKnee = "right_knee";
    public const string LeftAnkle = "left_ankle";
    public const string RightAnkle = "right_ankle";
}
