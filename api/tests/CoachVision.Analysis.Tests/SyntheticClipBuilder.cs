using CoachVision.Analysis;

namespace CoachVision.Analysis.Tests;

/// <summary>Builds raw-output documents in code for tests, standing in for a worker run on a real
/// clip. Heights are pixels, larger is lower, matching the raw output's own convention: a squat's
/// shoulders, hips and bar all move to a larger `y` at the bottom of the rep.</summary>
public static class SyntheticClipBuilder
{
    public const double FrameRate = 30.0;
    public const double StandingShoulderY = 650.0;
    public const double TorsoLength = 300.0;
    public const double BarAboveShoulders = 80.0;
    public const double PlateRadiusPx = 200.0;

    public static RawOutputDocument Build(
        int frameCount,
        Func<double, double> shoulderYOf,
        Func<double, double?>? barYOf = null,
        Func<double, double?>? radiusOf = null,
        bool includePose = true,
        bool includeBar = true,
        string workerVersion = "1.0.0")
    {
        var frames = new List<RawFrame>();
        for (var i = 0; i < frameCount; i++)
        {
            var t = i / FrameRate;
            var shoulderY = shoulderYOf(t);
            var hipY = shoulderY + TorsoLength;
            var kneeY = hipY + 250 - (0.6 * (shoulderY - StandingShoulderY)); // bends as the hip drops
            var ankleY = kneeY + 220;
            var x = 400.0;

            IReadOnlyDictionary<string, RawLandmark>? pose = includePose
                ? new Dictionary<string, RawLandmark>
                {
                    [LandmarkNames.LeftShoulder] = new() { X = x - 20, Y = shoulderY, Confidence = 0.95 },
                    [LandmarkNames.RightShoulder] = new() { X = x + 20, Y = shoulderY, Confidence = 0.95 },
                    [LandmarkNames.LeftHip] = new() { X = x - 15, Y = hipY, Confidence = 0.9 },
                    [LandmarkNames.RightHip] = new() { X = x + 15, Y = hipY, Confidence = 0.9 },
                    [LandmarkNames.LeftKnee] = new() { X = x - 15, Y = kneeY, Confidence = 0.88 },
                    [LandmarkNames.RightKnee] = new() { X = x + 15, Y = kneeY, Confidence = 0.88 },
                    [LandmarkNames.LeftAnkle] = new() { X = x - 15, Y = ankleY, Confidence = 0.85 },
                    [LandmarkNames.RightAnkle] = new() { X = x + 15, Y = ankleY, Confidence = 0.85 },
                }
                : null;

            RawBar? bar = null;
            if (includeBar)
            {
                var barY = barYOf is not null ? barYOf(t) : shoulderY - BarAboveShoulders;
                if (barY is not null)
                {
                    var radius = radiusOf is not null ? radiusOf(t) : PlateRadiusPx;
                    if (radius is not null)
                    {
                        bar = new RawBar { X = x, Y = barY.Value, RadiusPx = radius.Value, Confidence = 0.9 };
                    }
                }
            }

            frames.Add(new RawFrame { Index = i, T = t, Bar = bar, Pose = pose });
        }

        return new RawOutputDocument
        {
            SchemaVersion = "1.0",
            JobId = "job_01JC8YX4D2F7H1K9M3P5Q8S0TV",
            Worker = new RawWorker { Version = workerVersion, PoseModel = "mediapipe-pose-landmarker-full@1.2.2", BarDetector = "dfine-s-obj365@1" },
            Video = new RawVideo { Width = 1080, Height = 1920, FrameRate = FrameRate, DurationS = frameCount / FrameRate, FrameCount = frameCount },
            Frames = frames,
        };
    }

    /// <summary>A rising-cosine hump per rep: 0 at a rep's edges, 1 at its middle, so a squat
    /// standing-to-bottom-to-standing cycle repeats every `repSeconds`.</summary>
    public static Func<double, double> Reps(int count, double repSeconds, double amplitudePx, double baselineY = StandingShoulderY)
    {
        return t =>
        {
            if (t < 0 || t >= count * repSeconds)
            {
                return baselineY;
            }
            var phase = (t % repSeconds) / repSeconds;
            var profile = 0.5 * (1 - Math.Cos(2 * Math.PI * phase));
            return baselineY + (amplitudePx * profile);
        };
    }

    /// <summary>One rep's timing, for <see cref="ConcentricBarTrack"/>: descend for `DescendS`,
    /// hold at the bottom for `BottomHoldS`, rise at exactly `SpeedMps` for `ConcentricS`, then
    /// rest at the top for `RestS` before the next rep (or the clip's end).</summary>
    public readonly record struct RepSpec(double DescendS, double BottomHoldS, double ConcentricS, double SpeedMps, double RestS);

    /// <summary>A bar track built from exact per-rep concentric speeds: the bottom of each rep is
    /// placed exactly `SpeedMps * ConcentricS / metersPerPixel` pixels below the top, so the rise
    /// back to the top takes exactly `ConcentricS` seconds at exactly `SpeedMps` — geometrically
    /// consistent by construction, never overshooting past the top or falling short of it.</summary>
    public static Func<double, double?> ConcentricBarTrack(IReadOnlyList<RepSpec> reps, double plateDiameterMm = 450, double plateRadiusPx = PlateRadiusPx)
    {
        var metersPerPixel = (plateDiameterMm / 1000.0 / 2.0) / plateRadiusPx;
        var top = StandingShoulderY - BarAboveShoulders;
        var segments = new List<(double Start, double End, Func<double, double> Value)>();
        var t0 = 0.0;
        foreach (var rep in reps)
        {
            var bottom = top + (rep.SpeedMps * rep.ConcentricS / metersPerPixel);
            var descendStart = t0;
            var descendEnd = descendStart + rep.DescendS;
            var holdEnd = descendEnd + rep.BottomHoldS;
            var concentricEnd = holdEnd + rep.ConcentricS;
            var restEnd = concentricEnd + rep.RestS;
            segments.Add((descendStart, descendEnd, t => top + ((bottom - top) * ((t - descendStart) / rep.DescendS))));
            segments.Add((descendEnd, holdEnd, _ => bottom));
            segments.Add((holdEnd, concentricEnd, t => bottom - (rep.SpeedMps * (t - holdEnd) / metersPerPixel)));
            segments.Add((concentricEnd, restEnd, _ => top));
            t0 = restEnd;
        }
        return t =>
        {
            foreach (var (start, end, value) in segments)
            {
                if (t >= start && t < end)
                {
                    return value(t);
                }
            }
            return top;
        };
    }

    public static AnalysisRequest SquatRequest(IReadOnlyList<Feature> features, int plateDiameterMm = 450, int playbackWidth = 1080, int playbackHeight = 1920) => new()
    {
        Lift = Lift.Squat,
        Features = features,
        PlateDiameterMm = plateDiameterMm,
        PlaybackWidth = playbackWidth,
        PlaybackHeight = playbackHeight,
    };

    public static readonly IReadOnlyList<Feature> AllFeatures = new[] { Feature.BarPath, Feature.Velocity, Feature.RepCount, Feature.VerificationChecks };
}
