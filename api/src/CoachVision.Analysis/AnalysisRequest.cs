namespace CoachVision.Analysis;

/// <summary>The lift in the video (contract: `Lift`). Only <see cref="Squat"/> is analysed in v1;
/// every other value makes every requested feature `unanalysable: lift_not_supported`.</summary>
public enum Lift
{
    Squat,
    BenchPress,
    Deadlift,
    OverheadPress,
    Other,
}

/// <summary>An analysis to run (contract: `Feature`).</summary>
public enum Feature
{
    BarPath,
    Velocity,
    RepCount,
    VerificationChecks,
}

/// <summary>What the engine needs besides the raw output: the job's declared lift and features, the
/// plate diameter that scales pixels to metres, and the playback video's pixel size (the raw
/// output's frame size may differ from it, so bar-path pixel samples must be rescaled).</summary>
public sealed class AnalysisRequest
{
    public required Lift Lift { get; init; }
    public required IReadOnlyList<Feature> Features { get; init; }

    /// <summary>Outer diameter of the largest plates, millimetres (contract: `Equipment.plateDiameterMm`, default 450).</summary>
    public required int PlateDiameterMm { get; init; }

    public required int PlaybackWidth { get; init; }
    public required int PlaybackHeight { get; init; }
}
