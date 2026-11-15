using CoachVision.Analysis.Internal;

namespace CoachVision.Analysis;

/// <summary>Turns one raw output (contracts/internal/raw-output/v1.json) into the public results
/// (contracts/openapi/vision-v1.yaml: `Results`) the API will eventually store and serve (ADR 0017,
/// choice 2A). Pure: no I/O, no database, no model loading.</summary>
public static class AnalysisEngine
{
    public const string SchemaVersion = "1.0";

    /// <summary>How far the bar must fall and rise back, in plate radii, for a bottom to count as
    /// a rep. Confirmed against recorded clips (README.md § API: rep count figures).</summary>
    private const double BarRepDepthRadii = 1.0;

    /// <summary>The same, for the shoulders, in shoulder-to-hip lengths.</summary>
    private const double ShoulderRepDepthTorsos = 0.5;

    private const double MinRepGapSeconds = 0.5;

    /// <summary>A bar counts as found only when it is present in at least this share of the
    /// clip's frames; below it, there isn't enough of a track to scale or trust the bar at all
    /// (confirmed: a bar needs to be seen in at least 80% of frames to be usable). This gates
    /// `velocity` and `bar_path` on coverage, not on the detector's own per-frame confidence
    /// score, which is not calibrated as a probability and has no agreed cutoff.</summary>
    private const double MinBarFrameShare = 0.80;

    // `low_confidence` has its one justified use here: a measured peak speed above
    // `MaxPlausiblePeakMps` is not a real result capped to look plausible, it is evidence that
    // this rep's bar track isn't trustworthy, so the whole `velocity` feature is withheld rather
    // than reporting the capped number as if it were measured. Nothing else in this engine has a
    // confirmed basis for a confidence cutoff (the detector's score is not a calibrated
    // probability, and the body points it would be judged on have never been checked against
    // labelled positions — README.md § API: what is and isn't validated); `confidence` is still
    // computed and returned on every other analysed result, so a caller can apply its own cutoff
    // once one is confirmed.

    /// <summary>A bar reading whose own radius sits outside this band around the clip's median
    /// radius is treated as a frame with no bar at all: a radius far smaller or larger than every
    /// other reading in the clip is a detector mistake (a shadow, a second object), not a plate
    /// that briefly changed size. (assumption)</summary>
    private const double MinPlausibleRadiusRatio = 0.3;
    private const double MaxPlausibleRadiusRatio = 3.0;

    /// <summary>How far in time a "lightly smoothed" reading for the lockout and peak-speed
    /// searches looks either side of each sample, in seconds, not frames or pixels — so the same
    /// physical lift gives the same answer at 30 fps or 60 fps, and at any pixel scale. Long
    /// enough to damp a few milliseconds of timestamp jitter and a few pixels of detector noise;
    /// short enough to stay well inside a typical sub-second concentric phase. (assumption)</summary>
    internal const double SmoothingWindowS = 0.1;

    /// <summary>Lockout is not "back at the start level" (a lifter may settle noticeably lower or
    /// higher than where the rep began) and not "the first point recovering most of the depth"
    /// either (checked against every analysed rep in both recorded runs: a single threshold with
    /// no further check ends fast reps while the bar is still rising — on the fix8 run this moved
    /// `squat_16`'s mean by -9% to -12% and `squat_17` rep 1 by -9%, and sat exactly on `squat_10`
    /// rep 2's own plateau, one bad frame away from the fallback). It is two steps: (a) `LockoutGateShare`
    /// is a floor well below where any rep finishes, crossed once to mark where the climb to the
    /// top may be judged from (not itself the answer) — low enough that a stall before it can't
    /// be mistaken for the top, high enough that nothing mid-descent crosses it by accident; (b)
    /// from there, <see cref="LockoutIndex"/> walks forward to the first point after which the bar
    /// gets no higher, within `LockoutClimbLookAheadS`, by more than `LockoutClimbToleranceShare`
    /// of the depth — the actual top of the rise, wherever that lands. Checked across every
    /// analysed rep in both recorded runs: moving the gate from 0.85 to 0.95 changes no rep's mean
    /// by more than 3% (the recorded invariant test asserts this directly) — the gate only has to
    /// clear a mid-rise stall, the climb step decides the answer. (assumption)</summary>
    internal const double LockoutGateShare = 0.85;

    /// <summary>How much further (as a share of this rep's own depth) the bar may still climb
    /// within `LockoutClimbLookAheadS` of a candidate top before that point is rejected as not
    /// the top yet. Small: this is damping a measurement wobble right at the peak, not deciding
    /// between a stall and a finish (the gate already did that). (assumption)</summary>
    internal const double LockoutClimbToleranceShare = 0.01;

    /// <summary>How far ahead, in seconds, a candidate top is checked for further climb (see
    /// `LockoutClimbToleranceShare`) — long enough to see past a brief wobble right at the peak,
    /// short enough not to reach into a later, separate movement (a settling creep, a re-rack).
    /// (assumption)</summary>
    internal const double LockoutClimbLookAheadS = 0.3;

    /// <summary>A lockout search is never allowed past the shoulders' own end time for this rep
    /// (`rep_count`'s own `endS`) plus this margin: without it, the last rep in a set has nothing
    /// to stop the search running into the walk-back and re-rack afterwards, where the bar often
    /// ends up well above where the rep itself started. Generous enough for a genuinely slow
    /// finish to complete; short enough to exclude the next thing the lifter does with the bar.
    /// (assumption)</summary>
    internal const double LockoutSearchMarginAfterShoulderEndS = 1.0;

    /// <summary>No human lift moves the bar this fast. A peak reading above it is not reported
    /// capped to look plausible — that would still be a made-up number — it means this rep's own
    /// bar track isn't trustworthy, so the whole `velocity` feature comes back `unanalysable:
    /// low_confidence` instead (a result was produced but isn't trusted, so it is withheld).
    /// (assumption)</summary>
    private const double MaxPlausiblePeakMps = 5.0;

    /// <summary>The rule for whether a rep's concentric window has enough of the bar to measure
    /// at all: at least this many frames with a bar present, each a floor independent of the
    /// other (a rep could have two samples spanning its whole window, or many samples confined to
    /// a sliver of it). Below either, the whole `velocity` feature comes back `unanalysable:
    /// plates_not_found` — the contract's own wording, "no plate found in enough frames to scale
    /// or track the bar", applies just as much to one rep's window as to the whole clip.
    /// (assumption)</summary>
    private const int MinConcentricBarSamples = 2;

    /// <summary>The share of a rep's own concentric window (by frame count, a proxy for its
    /// duration since frames there are roughly evenly spaced) that must have a bar present. A gap
    /// in the middle with samples solidly on both sides still clears this; a rep whose few bar
    /// readings are bunched into a sliver of the window does not. (assumption)</summary>
    private const double MinConcentricBarCoverageShare = 0.5;

    public static AnalysisResults Analyze(RawOutputDocument rawInput, AnalysisRequest request)
    {
        var raw = WithPlausibleBarsOnly(rawInput);
        var modelVersion = ModelVersioning.Compute(raw.Worker);
        var wanted = request.Features;

        if (request.Lift != Lift.Squat)
        {
            return new AnalysisResults
            {
                SchemaVersion = SchemaVersion,
                BarPath = wanted.Contains(Feature.BarPath)
                    ? new BarPathUnanalysable { Reason = ReasonCodes.LiftNotSupported, ModelVersion = modelVersion }
                    : null,
                Velocity = wanted.Contains(Feature.Velocity)
                    ? new VelocityUnanalysable { Reason = ReasonCodes.LiftNotSupported, ModelVersion = modelVersion }
                    : null,
                RepCount = wanted.Contains(Feature.RepCount)
                    ? new RepCountUnanalysable { Reason = ReasonCodes.LiftNotSupported, ModelVersion = modelVersion }
                    : null,
                VerificationChecks = wanted.Contains(Feature.VerificationChecks)
                    ? new VerificationChecksResult { SquatDepth = new SquatDepthUnanalysable { Reason = ReasonCodes.LiftNotSupported, ModelVersion = modelVersion } }
                    : null,
            };
        }

        var shoulderTrack = Tracks.ShoulderTrack(raw.Frames);
        var shoulderWindows = Tracks.CountReps(shoulderTrack, ShoulderRepDepthTorsos, MinRepGapSeconds);

        RepCountResult repCountResult;
        string? repCascadeReason = null;
        // This floor (the counting algorithm's own data-sufficiency check, ported with it,
        // `Tracks.MinKnownFrames`) is not a confidence cutoff: below it the gap-filled track is
        // too sparse to mean anything, whatever the detector's own per-point confidence said.
        if (Tracks.KnownIndices(shoulderTrack.Heights).Length < Tracks.MinKnownFrames || shoulderTrack.Unit is null or 0)
        {
            repCascadeReason = ReasonCodes.PoseNotFound;
            repCountResult = new RepCountUnanalysable { Reason = ReasonCodes.PoseNotFound, ModelVersion = modelVersion };
        }
        else if (shoulderWindows!.Count == 0)
        {
            repCascadeReason = ReasonCodes.NoRepsDetected;
            repCountResult = new RepCountUnanalysable { Reason = ReasonCodes.NoRepsDetected, ModelVersion = modelVersion };
        }
        else
        {
            repCountResult = new RepCountAnalysed
            {
                Confidence = PoseConfidence(raw.Frames, LandmarkNames.LeftShoulder, LandmarkNames.RightShoulder),
                ModelVersion = modelVersion,
                Count = shoulderWindows.Count,
                Reps = shoulderWindows.Select((w, i) => new RepTiming
                {
                    Rep = i + 1,
                    StartS = shoulderTrack.Times[w.StartIndex],
                    BottomS = shoulderTrack.Times[w.BottomIndex],
                    EndS = shoulderTrack.Times[w.EndIndex],
                }).ToList(),
            };
        }

        var barTrack = Tracks.BarTrack(raw.Frames);
        var barKnown = Tracks.KnownIndices(barTrack.Heights).Length;
        var barShare = raw.Frames.Count > 0 ? (double)barKnown / raw.Frames.Count : 0.0;
        IReadOnlyList<RepWindow>? barWindows = null;
        string barOrVelocityReason;
        if (barShare < MinBarFrameShare || barTrack.Unit is null or 0)
        {
            // Not enough of the clip has a plate to scale or trust the bar at all
            // (`plates_not_found`'s own contract wording: "no plate found in enough frames").
            barOrVelocityReason = ReasonCodes.PlatesNotFound;
        }
        else if (repCascadeReason is not null)
        {
            // The shoulders could not be counted, so a bar count cannot be checked against them:
            // the bar is not trusted without that check, so velocity and bar path get the same
            // reason as rep_count rather than being judged on the bar alone.
            barOrVelocityReason = repCascadeReason;
        }
        else
        {
            barWindows = Tracks.CountReps(barTrack, BarRepDepthRadii, MinRepGapSeconds);
            barOrVelocityReason = barWindows is null || barWindows.Count != shoulderWindows!.Count
                ? ReasonCodes.BarTrackMismatch
                : string.Empty;
        }

        BarPathResult barPathResult;
        VelocityResult velocityResult;
        if (barOrVelocityReason.Length > 0)
        {
            // A circle that isn't trusted shouldn't be drawn either, so bar_path shares velocity's
            // gate rather than being offered on its own once the bar has failed either check.
            barPathResult = new BarPathUnanalysable { Reason = barOrVelocityReason, ModelVersion = modelVersion };
            velocityResult = new VelocityUnanalysable { Reason = barOrVelocityReason, ModelVersion = modelVersion };
        }
        else
        {
            barPathResult = BuildBarPath(raw, request, modelVersion);
            velocityResult = BuildVelocity(raw, request, barWindows!, shoulderWindows!, shoulderTrack, modelVersion);
        }

        SquatDepthResult squatDepthResult = repCascadeReason is not null
            ? new SquatDepthUnanalysable { Reason = repCascadeReason, ModelVersion = modelVersion }
            : BuildSquatDepth(raw, shoulderWindows!, modelVersion);

        return new AnalysisResults
        {
            SchemaVersion = SchemaVersion,
            BarPath = wanted.Contains(Feature.BarPath) ? barPathResult : null,
            Velocity = wanted.Contains(Feature.Velocity) ? velocityResult : null,
            RepCount = wanted.Contains(Feature.RepCount) ? repCountResult : null,
            VerificationChecks = wanted.Contains(Feature.VerificationChecks)
                ? new VerificationChecksResult { SquatDepth = squatDepthResult }
                : null,
        };
    }

    /// <summary>`raw` with any bar reading whose radius falls outside the plausible band around
    /// the clip's own median radius replaced by no bar at all (see `MinPlausibleRadiusRatio` /
    /// `MaxPlausibleRadiusRatio`). Pose is untouched. Done once, up front, so every later step —
    /// coverage, tracking, scaling, confidence — sees the same cleaned-up frames.</summary>
    private static RawOutputDocument WithPlausibleBarsOnly(RawOutputDocument raw)
    {
        var radii = raw.Frames.Where(f => f.Bar is not null).Select(f => f.Bar!.RadiusPx).ToList();
        if (radii.Count == 0)
        {
            return raw;
        }
        var median = Tracks.Median(radii);
        var minRadius = median * MinPlausibleRadiusRatio;
        var maxRadius = median * MaxPlausibleRadiusRatio;
        var sanitised = raw.Frames
            .Select(f => f.Bar is not null && (f.Bar.RadiusPx < minRadius || f.Bar.RadiusPx > maxRadius)
                ? new RawFrame { Index = f.Index, T = f.T, Bar = null, Pose = f.Pose }
                : f)
            .ToList();
        return new RawOutputDocument
        {
            SchemaVersion = raw.SchemaVersion,
            JobId = raw.JobId,
            Worker = raw.Worker,
            Video = raw.Video,
            Frames = sanitised,
        };
    }

    /// <summary>Share of `frames` with both named points present, times their mean confidence: a
    /// simple, honest figure, never higher than either term alone. Returned on every analysed
    /// result but never used to withhold one (no confirmed cutoff exists yet). (assumption)</summary>
    private static double PoseConfidence(IReadOnlyList<RawFrame> frames, string nameA, string nameB)
    {
        var present = 0;
        var confidenceSum = 0.0;
        foreach (var frame in frames)
        {
            if (frame.Pose is { } pose && pose.TryGetValue(nameA, out var a) && pose.TryGetValue(nameB, out var b))
            {
                present++;
                confidenceSum += (a.Confidence + b.Confidence) / 2;
            }
        }
        if (present == 0)
        {
            return 0;
        }
        var share = (double)present / frames.Count;
        var meanConfidence = confidenceSum / present;
        return share * meanConfidence;
    }

    private static double BarConfidence(IReadOnlyList<RawFrame> frames)
    {
        var present = 0;
        var confidenceSum = 0.0;
        foreach (var frame in frames)
        {
            if (frame.Bar is { } bar)
            {
                present++;
                confidenceSum += bar.Confidence;
            }
        }
        if (present == 0)
        {
            return 0;
        }
        var share = (double)present / frames.Count;
        return share * (confidenceSum / present);
    }

    private static BarPathResult BuildBarPath(RawOutputDocument raw, AnalysisRequest request, string modelVersion)
    {
        var plateRadiusM = request.PlateDiameterMm / 1000.0 / 2.0;
        var pxScale = (double)request.PlaybackWidth / raw.Video.Width;
        var pyScale = (double)request.PlaybackHeight / raw.Video.Height;
        var samples = new List<BarPathSample>();
        double? originX = null, originY = null;
        foreach (var frame in raw.Frames)
        {
            if (frame.Bar is not { } bar)
            {
                continue;
            }
            originX ??= bar.X;
            originY ??= bar.Y;
            var metersPerPixel = plateRadiusM / bar.RadiusPx;
            samples.Add(new BarPathSample
            {
                T = frame.T,
                X = (bar.X - originX.Value) * metersPerPixel,
                Y = (originY.Value - bar.Y) * metersPerPixel,
                // The contract types these pixel coordinates as >= 0 (the playback frame's own
                // bounds); the raw track does not — a circle whose centre falls just outside the
                // frame has a negative x or y, or one past the far edge. Metres (above) are left
                // as measured: only the drawing coordinates need to stay on the picture.
                Px = Math.Clamp(bar.X * pxScale, 0, request.PlaybackWidth),
                Py = Math.Clamp(bar.Y * pyScale, 0, request.PlaybackHeight),
            });
        }
        return new BarPathAnalysed
        {
            Confidence = BarConfidence(raw.Frames),
            ModelVersion = modelVersion,
            Samples = samples,
        };
    }

    private readonly record struct RepMeasurement(int Start, int End, int PresentCount, double CoverageShare, double MeanMps, double PeakMpsUncapped, double Confidence);

    private static VelocityResult BuildVelocity(
        RawOutputDocument raw, AnalysisRequest request,
        IReadOnlyList<RepWindow> barWindows, IReadOnlyList<RepWindow> shoulderWindows, Track shoulderTrack, string modelVersion)
    {
        var plateRadiusM = request.PlateDiameterMm / 1000.0 / 2.0;
        var measurements = new List<RepMeasurement>();
        var referenceStart = barWindows[0].StartIndex; // the first rep's own start; then each rep's own lockout
        for (var i = 0; i < barWindows.Count; i++)
        {
            var window = barWindows[i];
            // The segmenter's bottom also comes from the smoothed track, which can land a frame
            // or two before the true turning point; starting the concentric phase there pulls in
            // part of the descent, which still has a leftover negative (falling) reading. Reading
            // the raw track's own highest point between the window's start and end, instead of
            // the smoothed bottom index, finds the actual turning point.
            var concentricStart = RawTurningPointIndex(raw.Frames, window.StartIndex, window.EndIndex, highest: true);

            // The level the bar had just before this rep's own descent — not the clip's overall
            // highest point, which for the last rep runs into the walk-back and re-rack, where
            // the bar often ends up higher than where the rep itself started.
            var startLevelIndex = RawTurningPointIndex(raw.Frames, referenceStart, concentricStart, highest: false);
            var bottomY = raw.Frames[concentricStart].Bar!.Y;
            // No bar at all between the reference point and this bottom (should be rare, given
            // the clip-wide coverage gate) falls back to the bottom's own level: depth becomes 0,
            // and `LockoutIndex` falls back to the highest point reached in the window.
            var startLevel = raw.Frames[startLevelIndex].Bar?.Y ?? bottomY;
            var depthPx = bottomY - startLevel;

            // Never search past this rep's own end in the shoulders' timing, plus a short
            // margin: otherwise nothing stops the last rep's search running on into whatever
            // the lifter does with the bar next.
            var shoulderEndS = shoulderTrack.Times[shoulderWindows[i].EndIndex];
            var timeBoundIndex = TimeBoundIndex(raw.Frames, concentricStart, window.EndIndex, shoulderEndS + LockoutSearchMarginAfterShoulderEndS);
            var searchEnd = Math.Min(window.EndIndex, timeBoundIndex);

            var concentricEnd = LockoutIndex(raw.Frames, concentricStart, searchEnd, bottomY, depthPx, LockoutGateShare, LockoutClimbToleranceShare, LockoutClimbLookAheadS);
            referenceStart = concentricEnd;

            var total = concentricEnd - concentricStart + 1;
            var present = Enumerable.Range(concentricStart, total)
                .Select(index => raw.Frames[index])
                .Where(frame => frame.Bar is not null)
                .ToList();
            var coverageShare = total > 0 ? (double)present.Count / total : 0;

            double meanMps = 0, peakMpsUncapped = 0, repConfidence = 0;
            if (present.Count >= MinConcentricBarSamples)
            {
                // One scale per rep, not per frame: the plate's measured radius can vary
                // noticeably within a clip, and scaling frame by frame would turn that jitter
                // into fake speed changes. A single median for the whole rep keeps the speed
                // from a cycle of radius noise while still tracking a camera that moves between
                // reps. (assumption)
                var repRadius = Tracks.Median(present.Select(f => f.Bar!.RadiusPx).ToList());
                var metersPerPixel = plateRadiusM / repRadius;
                meanMps = MeanConcentricSpeedMps(present, metersPerPixel);
                // Uncapped here: whether it stands is decided once, below, across every rep —
                // reporting a capped number for this one rep while silently failing another
                // would be exactly the made-up-value problem this is meant to avoid.
                peakMpsUncapped = PeakConcentricSpeedMps(present, metersPerPixel, SmoothingWindowS, double.PositiveInfinity);
                repConfidence = present.Average(f => f.Bar!.Confidence);
            }

            measurements.Add(new RepMeasurement(concentricStart, concentricEnd, present.Count, coverageShare, meanMps, peakMpsUncapped, repConfidence));
        }

        // A rep whose concentric window doesn't have enough of the bar to measure at all makes a
        // made-up number (there would be nothing to compute `meanConcentricMps`/`peakConcentricMps`
        // from); the whole feature is withheld, not just that rep, since `RepVelocity` has no
        // partial or per-rep unanalysable form.
        if (measurements.Any(m => m.PresentCount < MinConcentricBarSamples || m.CoverageShare < MinConcentricBarCoverageShare))
        {
            return new VelocityUnanalysable { Reason = ReasonCodes.PlatesNotFound, ModelVersion = modelVersion };
        }
        // A peak above what any human lift can reach means this rep's track isn't trustworthy;
        // reporting it capped would still be a made-up number, so the whole feature is withheld
        // instead — `low_confidence`'s one justified use here.
        if (measurements.Any(m => m.PeakMpsUncapped > MaxPlausiblePeakMps))
        {
            return new VelocityUnanalysable { Reason = ReasonCodes.LowConfidence, ModelVersion = modelVersion };
        }

        var reps = measurements.Select((m, i) => new RepVelocity
        {
            Rep = i + 1,
            MeanConcentricMps = m.MeanMps,
            PeakConcentricMps = m.PeakMpsUncapped,
            Confidence = m.Confidence,
        }).ToList();

        return new VelocityAnalysed
        {
            Confidence = BarConfidence(raw.Frames),
            ModelVersion = modelVersion,
            Reps = reps,
        };
    }

    /// <summary>Net vertical displacement from the first to the last of `frames` (each with a
    /// bar), over the elapsed time between them — not an average of per-frame speeds: the raw
    /// output's own frame times are uneven (phone video, variable frame rate), and averaging
    /// speeds over uneven steps weights the short ones more than the time they actually cover.
    /// A rep that stalls part-way and then finishes is measured correctly by the same reasoning:
    /// the stall's time still counts, because only the two endpoints and the time between them
    /// matter here.</summary>
    internal static double MeanConcentricSpeedMps(IReadOnlyList<RawFrame> frames, double metersPerPixel)
    {
        var totalDt = frames[^1].T - frames[0].T;
        if (totalDt <= 0)
        {
            return 0;
        }
        var totalRisenPx = frames[0].Bar!.Y - frames[^1].Bar!.Y;
        return Math.Max(0, totalRisenPx * metersPerPixel / totalDt);
    }

    /// <summary>The fastest rise seen over any `windowS`-wide (or shorter, at either end) stretch
    /// of `frames`, scaled to metres per second and capped at `hardBoundMps`. Looking over a
    /// short span of time, not a single step between consecutive raw samples, keeps a couple of
    /// close timestamps or a few pixels of jitter from producing a spike no real lift could
    /// reach; the cap is a last-resort sanity check on top of that, not the main defence.</summary>
    internal static double PeakConcentricSpeedMps(IReadOnlyList<RawFrame> frames, double metersPerPixel, double windowS, double hardBoundMps)
    {
        var peak = 0.0;
        for (var i = 0; i < frames.Count; i++)
        {
            var j = i;
            for (var k = i + 1; k < frames.Count; k++)
            {
                j = k;
                if (frames[k].T - frames[i].T >= windowS)
                {
                    break;
                }
            }
            if (j == i)
            {
                continue;
            }
            var dt = frames[j].T - frames[i].T;
            if (dt <= 0)
            {
                continue;
            }
            var risen = frames[i].Bar!.Y - frames[j].Bar!.Y;
            var speed = risen * metersPerPixel / dt;
            if (speed > peak)
            {
                peak = speed;
            }
        }
        return Math.Min(Math.Max(0, peak), hardBoundMps);
    }

    /// <summary>The last frame in `[from, to]` at which the bar's own raw height reaches its
    /// highest (`highest: true`, the physical bottom of the lift) or lowest (`highest: false`)
    /// point. The last, not the first: a paused rep holds the bottom flat for a stretch before
    /// the real rise begins, and starting the concentric phase at the first frame of that plateau
    /// would count the pause's own time as part of the rise and understate the speed. The rep
    /// window the segmenter found comes from the heavier smoothing the counter uses, which lags a
    /// sharp turn by a frame or two; using its bottom index directly as the concentric phase's
    /// start would fold in a frame or two still descending, with a leftover negative (falling)
    /// reading, and understate the speed the same way. Unlike the end (<see cref="LockoutIndex"/>),
    /// the start is a single, unambiguous peak (plus, at most, a held pause), not a long noisy
    /// plateau a few pixels of jitter could wander across — no smoothing is used here, since
    /// averaging a sharp, asymmetric turn (a squat's descent and ascent rarely take the same time)
    /// can shift the smoothed peak away from the true one instead of just denoising it.</summary>
    internal static int RawTurningPointIndex(IReadOnlyList<RawFrame> frames, int from, int to, bool highest)
    {
        var bestIndex = from;
        double? bestY = null;
        for (var i = from; i <= to; i++)
        {
            var y = frames[i].Bar?.Y;
            if (y is null)
            {
                continue;
            }
            if (bestY is null || (highest ? y >= bestY : y <= bestY))
            {
                bestY = y;
                bestIndex = i;
            }
        }
        return bestIndex;
    }

    /// <summary>Where lockout happens, in two steps over the lightly smoothed track (see
    /// `TimeWindowedSamples`): (a) find the first frame, scanning forward from `concentricStart`
    /// to `outerEnd`, at which the bar has recovered at least `gateShare` of this rep's own
    /// descent (`bottomY` to `bottomY - depthPx`) — not the answer itself, just a floor low enough
    /// that a stall can't be mistaken for it and high enough that nothing mid-descent crosses it
    /// by accident; (b) from there, walk forward to the actual top of the rise: the first point
    /// after which the bar gets no higher, within `climbLookAheadS`, by more than
    /// `climbToleranceShare` of the depth. A rep that never reaches the gate (finishes
    /// noticeably short, or a window cut short by the next rep's own descent) falls back to the
    /// highest point reached anywhere in the window.</summary>
    internal static int LockoutIndex(
        IReadOnlyList<RawFrame> frames, int concentricStart, int outerEnd, double bottomY, double depthPx,
        double gateShare, double climbToleranceShare, double climbLookAheadS)
    {
        if (depthPx <= 0)
        {
            return RawTurningPointIndex(frames, concentricStart, outerEnd, highest: false);
        }
        var samples = TimeWindowedSamples(frames, concentricStart, outerEnd, SmoothingWindowS);
        double RecoveredShare(double y) => (bottomY - y) / depthPx;

        var gateIndex = -1;
        for (var i = 0; i < samples.Count; i++)
        {
            if (RecoveredShare(samples[i].Smoothed) >= gateShare)
            {
                gateIndex = i;
                break;
            }
        }
        if (gateIndex < 0)
        {
            return RawTurningPointIndex(frames, concentricStart, outerEnd, highest: false);
        }

        for (var i = gateIndex; i < samples.Count; i++)
        {
            var share = RecoveredShare(samples[i].Smoothed);
            var climbsFurther = false;
            for (var j = i + 1; j < samples.Count && samples[j].T - samples[i].T <= climbLookAheadS; j++)
            {
                if (RecoveredShare(samples[j].Smoothed) > share + climbToleranceShare)
                {
                    climbsFurther = true;
                    break;
                }
            }
            if (!climbsFurther)
            {
                // Smoothing a sharp corner (a clean rise meeting a dead flat top) pulls the
                // smoothed value right at the corner part-way back toward the still-rising frame
                // before it, so the scan above can land a frame late. Walking back while the raw
                // track is already at least as high as the stop point corrects for that, without
                // reopening the search to noise further back (a frame that isn't already there
                // breaks the walk). The walk-back is bounded by time, not by a frame count: the
                // lag this corrects for comes from `TimeWindowedSamples`'s own window
                // (`SmoothingWindowS`), so the correction can only ever need to reach half that
                // window back, regardless of how many raw frames that time happens to span at the
                // clip's frame rate — a fixed frame count would under-reach at a low frame rate
                // and over-reach (back past where the lag actually starts) at a high one.
                var stopIndex = samples[i].Index;
                var stopT = frames[stopIndex].T;
                var stopY = frames[stopIndex].Bar!.Y;
                var refined = stopIndex;
                for (var k = stopIndex - 1; k >= concentricStart && stopT - frames[k].T <= SmoothingWindowS / 2; k--)
                {
                    var y = frames[k].Bar?.Y;
                    if (y is null || y > stopY)
                    {
                        break;
                    }
                    refined = k;
                }
                return refined;
            }
        }
        return samples[^1].Index;
    }

    /// <summary>The last frame in `[from, to]` whose time does not exceed `maxT` (frames are in
    /// ascending time order); `from` itself if even that already exceeds it.</summary>
    internal static int TimeBoundIndex(IReadOnlyList<RawFrame> frames, int from, int to, double maxT)
    {
        var best = from;
        for (var i = from; i <= to; i++)
        {
            if (frames[i].T <= maxT)
            {
                best = i;
            }
            else
            {
                break;
            }
        }
        return best;
    }

    internal readonly record struct TimedSample(int Index, double T, double Smoothed);

    /// <summary>The bar's raw height at every frame with a bar in `[from, to]`, each replaced by
    /// the mean of every present sample within `windowS / 2` seconds of its own time (not frames,
    /// so the same physical window covers more samples at a higher frame rate, not fewer frames at
    /// any rate) — damping a few milliseconds of timestamp jitter and a few pixels of detector
    /// noise without the lag a fixed, wider frame-count filter would add.</summary>
    internal static List<TimedSample> TimeWindowedSamples(IReadOnlyList<RawFrame> frames, int from, int to, double windowS)
    {
        var present = new List<(int Index, double T, double Y)>();
        for (var i = from; i <= to; i++)
        {
            var y = frames[i].Bar?.Y;
            if (y is not null)
            {
                present.Add((i, frames[i].T, y.Value));
            }
        }
        var half = windowS / 2;
        var result = new List<TimedSample>(present.Count);
        foreach (var sample in present)
        {
            var sum = 0.0;
            var count = 0;
            foreach (var other in present)
            {
                if (Math.Abs(other.T - sample.T) <= half)
                {
                    sum += other.Y;
                    count++;
                }
            }
            result.Add(new TimedSample(sample.Index, sample.T, count > 0 ? sum / count : sample.Y));
        }
        return result;
    }

    private static readonly (string Hip, string Knee, string Ankle)[] Sides =
    {
        (LandmarkNames.LeftHip, LandmarkNames.LeftKnee, LandmarkNames.LeftAnkle),
        (LandmarkNames.RightHip, LandmarkNames.RightKnee, LandmarkNames.RightAnkle),
    };

    private static SquatDepthResult BuildSquatDepth(RawOutputDocument raw, IReadOnlyList<RepWindow> shoulderWindows, string modelVersion)
    {
        var reps = new List<RepDepth>();
        var confidences = new List<double>();
        foreach (var window in shoulderWindows)
        {
            var rep = reps.Count + 1;
            var bottomPose = raw.Frames[window.BottomIndex].Pose;
            var side = bottomPose is null
                ? null
                : Sides
                    .Where(s => bottomPose.ContainsKey(s.Hip) && bottomPose.ContainsKey(s.Knee))
                    .OrderByDescending(s => (bottomPose[s.Hip].Confidence + bottomPose[s.Knee].Confidence) / 2)
                    .Cast<(string Hip, string Knee, string Ankle)?>()
                    .FirstOrDefault();
            if (side is null)
            {
                // Presence, not confidence: neither side has both a hip and a knee reported at
                // the bottom frame at all, so there is nothing to judge depth from for this rep.
                // Required by the schema: RepDepth has no per-rep unanalysable form, so one rep
                // missing this fails the whole check rather than a partial result.
                return new SquatDepthUnanalysable { Reason = ReasonCodes.PoseNotFound, ModelVersion = modelVersion };
            }
            var (hipName, kneeName, ankleName) = side.Value;
            var hip = bottomPose![hipName];
            var knee = bottomPose[kneeName];
            var passed = hip.Y > knee.Y;

            var angles = new List<double>();
            var frameConfidences = new List<double>();
            for (var index = window.StartIndex; index <= window.EndIndex; index++)
            {
                var pose = raw.Frames[index].Pose;
                if (pose is null || !pose.TryGetValue(hipName, out var h) || !pose.TryGetValue(kneeName, out var k) || !pose.TryGetValue(ankleName, out var a))
                {
                    continue;
                }
                angles.Add(KneeGeometry.AngleDegrees((h.X, h.Y), (k.X, k.Y), (a.X, a.Y)));
                frameConfidences.Add((h.Confidence + k.Confidence + a.Confidence) / 3);
            }
            if (angles.Count == 0)
            {
                // Same presence-based reasoning: the chosen side never has hip, knee and ankle
                // together anywhere in the rep's window, so no knee angle can be measured at all.
                return new SquatDepthUnanalysable { Reason = ReasonCodes.PoseNotFound, ModelVersion = modelVersion };
            }
            var repConfidence = frameConfidences.Average();
            confidences.Add(repConfidence);
            reps.Add(new RepDepth
            {
                Rep = rep,
                Passed = passed,
                MinKneeAngleDeg = angles.Min(),
                Confidence = repConfidence,
            });
        }
        return new SquatDepthAnalysed
        {
            Confidence = confidences.Count > 0 ? confidences.Average() : 0,
            ModelVersion = modelVersion,
            AllRepsPassed = reps.All(r => r.Passed),
            Reps = reps,
        };
    }
}
