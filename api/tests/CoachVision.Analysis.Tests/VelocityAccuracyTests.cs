using CoachVision.Analysis;

namespace CoachVision.Analysis.Tests;

/// <summary>Direct tests of the engine's mean and peak concentric speed formulas, against the
/// exact numbers a past review gave, plus the frame-rate- and pixel-scale-independence those
/// formulas are meant to have.</summary>
public sealed class VelocityAccuracyTests
{
    private static RawFrame BarFrame(int index, double t, double y, double radius = 200.0) =>
        new() { Index = index, T = t, Bar = new RawBar { X = 0, Y = y, RadiusPx = radius, Confidence = 0.9 }, Pose = null };

    [Fact]
    public void MeanConcentricSpeedMps_is_net_displacement_over_elapsed_time_not_an_average_of_steps()
    {
        // dt = [0.01, 0.01, 0.48] s, rises = [10, 10, 880] px, 0.001 m/px: an average of the three
        // per-step speeds (1.0, 1.0, 1.833) gives 1.28 m/s; the true mean is 0.9 m over 0.5 s = 1.8.
        const double metersPerPixel = 0.001;
        var frames = new[]
        {
            BarFrame(0, 0.00, y: 900.0),
            BarFrame(1, 0.01, y: 890.0),
            BarFrame(2, 0.02, y: 880.0),
            BarFrame(3, 0.50, y: 0.0),
        };

        var mean = AnalysisEngine.MeanConcentricSpeedMps(frames, metersPerPixel);

        Assert.Equal(1.8, mean, precision: 9);
    }

    [Fact]
    public void MeanConcentricSpeedMps_matches_a_known_speed_under_a_jittered_30fps_clock()
    {
        const double speedMps = 0.6;
        const double metersPerPixel = 0.225 / 200.0;
        var clockRng = new Random(20261116);
        var frames = new List<RawFrame>();
        double t = 0;
        double y = 1000.0;
        for (var i = 0; i < 24; i++) // ~0.8 s at a jittered 30 fps
        {
            frames.Add(BarFrame(i, t, y));
            var dt = (1.0 / 30) + ((clockRng.NextDouble() * 0.006) - 0.003); // +/- 3 ms of clock jitter
            y -= speedMps * dt / metersPerPixel;
            t += dt;
        }

        var mean = AnalysisEngine.MeanConcentricSpeedMps(frames, metersPerPixel);

        Assert.InRange(mean, speedMps * 0.95, speedMps * 1.05);
    }

    [Fact]
    public void PeakConcentricSpeedMps_survives_jitter_and_irregular_timestamps_within_20_percent()
    {
        // A constant 0.6 m/s rise, +/- 3 px of detector jitter, and a few pairs of timestamps
        // only 2 ms apart (the kind of irregular capture a phone video can produce).
        const double speedMps = 0.6;
        const double metersPerPixel = 0.225 / 200.0;
        var rng = new Random(20261116);
        var frames = new List<RawFrame>();
        double t = 0;
        double y = 1000.0;
        for (var i = 0; i < 24; i++)
        {
            frames.Add(BarFrame(i, t, y + ((rng.NextDouble() * 6) - 3)));
            var dt = i % 5 == 0 ? 0.002 : 1.0 / 30; // the occasional 2 ms-apart pair
            y -= speedMps * dt / metersPerPixel;
            t += dt;
        }

        var peak = AnalysisEngine.PeakConcentricSpeedMps(frames, metersPerPixel, windowS: 0.1, hardBoundMps: 5.0);

        // +/- 20%: generous enough for the jitter and irregular spacing here, but still far below
        // the plausibility cap, so a genuine measurement artefact would still stand out.
        Assert.InRange(peak, speedMps * 0.8, speedMps * 1.2);
    }

    [Fact]
    public void PeakConcentricSpeedMps_never_exceeds_the_hard_sanity_bound()
    {
        // Two frames 1 ms apart with an absurd pixel jump: without a floor on dt or a cap, this
        // alone would produce a nonsensical peak.
        var frames = new[]
        {
            BarFrame(0, 0.000, y: 1000.0),
            BarFrame(1, 0.001, y: 0.0),
        };

        var peak = AnalysisEngine.PeakConcentricSpeedMps(frames, metersPerPixel: 0.001, windowS: 0.1, hardBoundMps: 5.0);

        Assert.Equal(5.0, peak);
    }

    /// <summary>Builds a single clean rep's bar track at a given frame rate and pixel scale, so
    /// the full engine's result can be compared across both.</summary>
    private static RawOutputDocument BuildClip(double frameRate, double pixelScale, double speedMps = 0.6)
    {
        const double descendS = 1.0;
        const double concentricS = 0.6;
        const double restS = 1.0;
        var plateRadiusPx = 200.0 * pixelScale;
        var top = (650.0 - 80.0) * pixelScale;
        var bottom = top + (speedMps * concentricS / (0.225 / plateRadiusPx));
        var frameCount = (int)((descendS + concentricS + restS) * frameRate);
        var frames = new List<RawFrame>();
        var poseFrames = new List<IReadOnlyDictionary<string, RawLandmark>?>();
        for (var i = 0; i < frameCount; i++)
        {
            var t = i / frameRate;
            double barY;
            if (t < descendS)
            {
                barY = top + ((bottom - top) * (t / descendS));
            }
            else if (t < descendS + concentricS)
            {
                barY = bottom - (speedMps * (t - descendS) / (0.225 / plateRadiusPx));
            }
            else
            {
                barY = top;
            }
            frames.Add(BarFrame(i, t, barY, plateRadiusPx));
        }

        // The shoulders need their own clean single rep, timed to span the same clip, so the
        // bar's own rep count has something to agree with.
        var shoulderProfile = SyntheticClipBuilder.Reps(1, descendS + concentricS + restS, 250 * pixelScale);
        var shoulderFrames = new List<RawFrame>();
        for (var i = 0; i < frameCount; i++)
        {
            var t = i / frameRate;
            var shoulderY = shoulderProfile(t);
            var hipY = shoulderY + (300 * pixelScale);
            var kneeY = hipY + (250 * pixelScale);
            var ankleY = kneeY + (220 * pixelScale);
            var x = 400.0 * pixelScale;
            var pose = new Dictionary<string, RawLandmark>
            {
                [LandmarkNames.LeftShoulder] = new() { X = x - 20, Y = shoulderY, Confidence = 0.95 },
                [LandmarkNames.RightShoulder] = new() { X = x + 20, Y = shoulderY, Confidence = 0.95 },
                [LandmarkNames.LeftHip] = new() { X = x - 15, Y = hipY, Confidence = 0.9 },
                [LandmarkNames.RightHip] = new() { X = x + 15, Y = hipY, Confidence = 0.9 },
                [LandmarkNames.LeftKnee] = new() { X = x - 15, Y = kneeY, Confidence = 0.88 },
                [LandmarkNames.RightKnee] = new() { X = x + 15, Y = kneeY, Confidence = 0.88 },
                [LandmarkNames.LeftAnkle] = new() { X = x - 15, Y = ankleY, Confidence = 0.85 },
                [LandmarkNames.RightAnkle] = new() { X = x + 15, Y = ankleY, Confidence = 0.85 },
            };
            shoulderFrames.Add(new RawFrame { Index = i, T = t, Bar = frames[i].Bar, Pose = pose });
        }

        return new RawOutputDocument
        {
            SchemaVersion = "1.0",
            JobId = "job_01JC8YX4D2F7H1K9M3P5Q8S0TV",
            Worker = new RawWorker { Version = "1.0.0", PoseModel = "m@1", BarDetector = "b@1" },
            Video = new RawVideo { Width = (int)(1080 * pixelScale), Height = (int)(1920 * pixelScale), FrameRate = frameRate, DurationS = frameCount / frameRate, FrameCount = frameCount },
            Frames = shoulderFrames,
        };
    }

    [Theory]
    [InlineData(30.0)]
    [InlineData(60.0)]
    [InlineData(120.0)]
    [InlineData(240.0)]
    public void LockoutIndex_lands_at_the_same_time_and_mean_on_a_sharp_corner_regardless_of_frame_rate(double frameRate)
    {
        // A clean rise meeting a dead flat top (the shape the backward correction inside
        // `LockoutIndex` exists for): at every frame rate from 30 to 240 fps, the corner must be
        // found at the same real time and give the same mean speed, since the correction is now
        // bounded by time (half of `SmoothingWindowS`) rather than a fixed number of raw frames.
        const double speedMps = 0.6;
        const double plateRadiusPx = 200.0;
        const double metersPerPixel = 0.225 / plateRadiusPx;
        const double top = 570.0;
        const double riseDurationS = 0.6;
        const double depthPx = speedMps * riseDurationS / metersPerPixel;
        const double bottom = top + depthPx;

        var frames = new List<RawFrame>();
        var riseFrameCount = (int)(riseDurationS * frameRate);
        for (var i = 0; i <= riseFrameCount; i++)
        {
            var t = i / frameRate;
            frames.Add(BarFrame(i, t, bottom - ((bottom - top) * (i / (double)riseFrameCount))));
        }
        var topReachedAt = riseFrameCount / frameRate;
        var holdFrameCount = (int)(1.0 * frameRate);
        for (var i = 1; i <= holdFrameCount; i++) // a dead flat top right after the corner
        {
            var t = topReachedAt + (i / frameRate);
            frames.Add(BarFrame(frames.Count, t, top));
        }

        var lockout = AnalysisEngine.LockoutIndex(
            frames, concentricStart: 0, outerEnd: frames.Count - 1, bottomY: bottom, depthPx: depthPx,
            AnalysisEngine.LockoutGateShare, AnalysisEngine.LockoutClimbToleranceShare, AnalysisEngine.LockoutClimbLookAheadS);

        Assert.InRange(frames[lockout].T, topReachedAt - 0.02, topReachedAt + 0.02);

        var windowed = frames.Take(lockout + 1).ToList();
        var mean = AnalysisEngine.MeanConcentricSpeedMps(windowed, metersPerPixel);
        Assert.InRange(mean, speedMps * 0.98, speedMps * 1.02);
    }

    [Fact]
    public void The_same_lift_at_30_and_60_fps_gives_the_same_mean_speed_within_2_percent()
    {
        const double speedMps = 0.6;
        var request = SyntheticClipBuilder.SquatRequest(new[] { Feature.Velocity });

        var at30 = Assert.IsType<VelocityAnalysed>(AnalysisEngine.Analyze(BuildClip(30, 1.0, speedMps), request).Velocity);
        var at60 = Assert.IsType<VelocityAnalysed>(AnalysisEngine.Analyze(BuildClip(60, 1.0, speedMps), request).Velocity);

        Assert.Single(at30.Reps);
        Assert.Single(at60.Reps);
        var ratio = at60.Reps[0].MeanConcentricMps / at30.Reps[0].MeanConcentricMps;
        Assert.InRange(ratio, 0.98, 1.02);
    }

    [Fact]
    public void The_same_lift_at_twice_the_pixel_scale_gives_the_same_mean_speed_within_2_percent()
    {
        const double speedMps = 0.6;
        var request = SyntheticClipBuilder.SquatRequest(new[] { Feature.Velocity });

        var at1x = Assert.IsType<VelocityAnalysed>(AnalysisEngine.Analyze(BuildClip(30, 1.0, speedMps), request).Velocity);
        var at2x = Assert.IsType<VelocityAnalysed>(AnalysisEngine.Analyze(BuildClip(30, 2.0, speedMps), request).Velocity);

        Assert.Single(at1x.Reps);
        Assert.Single(at2x.Reps);
        var ratio = at2x.Reps[0].MeanConcentricMps / at1x.Reps[0].MeanConcentricMps;
        Assert.InRange(ratio, 0.98, 1.02);
    }

    [Fact]
    public void A_rep_that_stalls_mid_rise_then_finishes_has_the_stalls_time_included_in_the_mean()
    {
        const double descendS = 1.0;
        const double stallS = 0.6;
        const double finishS = 0.3;
        const double speedMps = 0.6;
        var plateRadiusPx = 200.0;
        var top = 570.0;
        var midBottom = 760.0; // where the stall happens, well short of the eventual target
        var finalBottom = top + (speedMps * finishS / (0.225 / plateRadiusPx));
        var frames = new List<RawFrame>();
        var t = 0.0;
        for (var i = 0; i < 30; i++) // descend
        {
            t = i / 30.0;
            frames.Add(BarFrame(i, t, finalBottom + ((finalBottom - top) * (1 - (t / descendS)))));
        }
        var stallStart = t;
        for (var i = 0; i < (int)(stallS * 30); i++) // stall, not yet at the target
        {
            t = stallStart + (i / 30.0);
            frames.Add(BarFrame(30 + i, t, midBottom));
        }
        var finishStart = t;
        var finishFrames = (int)(finishS * 30);
        for (var i = 0; i <= finishFrames; i++) // finish: from the stall level down to the true target
        {
            t = finishStart + (i / 30.0);
            var level = midBottom - ((midBottom - top) * (i / (double)finishFrames));
            frames.Add(BarFrame(frames.Count, t, level));
        }
        for (var i = 0; i < 30; i++) // rest, well clear at the top
        {
            t += 1.0 / 30;
            frames.Add(BarFrame(frames.Count, t, top));
        }

        var bottomIndex = AnalysisEngine.RawTurningPointIndex(frames, 0, frames.Count - 1, highest: true);
        var lockout = AnalysisEngine.LockoutIndex(
            frames, bottomIndex, frames.Count - 1, frames[bottomIndex].Bar!.Y, frames[bottomIndex].Bar!.Y - top,
            AnalysisEngine.LockoutGateShare, AnalysisEngine.LockoutClimbToleranceShare, AnalysisEngine.LockoutClimbLookAheadS);
        var windowed = frames.Skip(bottomIndex).Take(lockout - bottomIndex + 1).ToList();
        var mean = AnalysisEngine.MeanConcentricSpeedMps(windowed, 0.225 / plateRadiusPx);

        // The true mean over bottom-to-lockout, stall included: total rise / total time.
        var expectedTotalTime = windowed[^1].T - windowed[0].T;
        var expectedTotalRise = windowed[0].Bar!.Y - windowed[^1].Bar!.Y;
        var expectedMean = expectedTotalRise * (0.225 / plateRadiusPx) / expectedTotalTime;

        Assert.InRange(mean, expectedMean * 0.98, expectedMean * 1.02);
        // Sanity: the stall measurably drags the mean well below the finishing segment's own
        // (much faster) instantaneous speed, proving the stall's time was not discarded.
        Assert.True(mean < speedMps * 0.9, $"mean {mean} should be well under the finishing speed {speedMps}, stall time must still count");
    }

    [Fact]
    public void A_paused_rep_at_the_bottom_still_gives_one_clean_concentric_phase()
    {
        const double speedMps = 0.6;
        var plateRadiusPx = 200.0;
        var top = 570.0;
        var bottom = top + (speedMps * 0.6 / (0.225 / plateRadiusPx));
        var frames = new List<RawFrame>();
        var t = 0.0;
        for (var i = 0; i < 30; i++)
        {
            t = i / 30.0;
            frames.Add(BarFrame(i, t, top + ((bottom - top) * (t / 1.0))));
        }
        var pauseStart = t;
        for (var i = 0; i < 15; i++) // 0.5 s pause at the bottom
        {
            t = pauseStart + (i / 30.0);
            frames.Add(BarFrame(frames.Count, t, bottom));
        }
        var riseStart = t;
        for (var i = 0; i <= 18; i++) // 0.6 s rise at the known speed
        {
            t = riseStart + (i / 30.0);
            frames.Add(BarFrame(frames.Count, t, bottom - (speedMps * (t - riseStart) / (0.225 / plateRadiusPx))));
        }

        var bottomIndex = AnalysisEngine.RawTurningPointIndex(frames, 0, frames.Count - 1, highest: true);
        var lockout = AnalysisEngine.LockoutIndex(
            frames, bottomIndex, frames.Count - 1, frames[bottomIndex].Bar!.Y, frames[bottomIndex].Bar!.Y - top,
            AnalysisEngine.LockoutGateShare, AnalysisEngine.LockoutClimbToleranceShare, AnalysisEngine.LockoutClimbLookAheadS);
        var windowed = frames.Skip(bottomIndex).Take(lockout - bottomIndex + 1).ToList();
        var mean = AnalysisEngine.MeanConcentricSpeedMps(windowed, 0.225 / plateRadiusPx);

        // The pause sits entirely at the bottom, before the true rise starts: the bottom index
        // itself should land at (or very near) the end of the pause, not inside the descent, so
        // the measured mean should be close to the known rising speed, not diluted by the pause.
        Assert.InRange(mean, speedMps * 0.95, speedMps * 1.05);
    }

    /// <summary>A rep: descend from `top` to `bottom` (reached by the known concentric speed and
    /// duration) over `descendS`, rise back to `top` at `speedMps` over `concentricS`, then hold
    /// at `top` for `holdS`. Returns the frames and the time `top` is first reached.</summary>
    private static (List<RawFrame> Frames, double TopReachedAtS) OneCleanRep(
        double startAtS, double top, double bottom, double speedMps, double metersPerPixel,
        double descendS, double concentricS, double holdS, double frameRate = 30.0)
    {
        var frames = new List<RawFrame>();
        var t = startAtS;
        var end = startAtS + descendS;
        while (t < end)
        {
            frames.Add(BarFrame(frames.Count, t, top + ((bottom - top) * ((t - startAtS) / descendS))));
            t += 1.0 / frameRate;
        }
        var concentricStart = t;
        end = concentricStart + concentricS;
        while (t < end)
        {
            frames.Add(BarFrame(frames.Count, t, bottom - (speedMps * (t - concentricStart) / metersPerPixel)));
            t += 1.0 / frameRate;
        }
        var topReachedAtS = t;
        end = t + holdS;
        while (t < end)
        {
            frames.Add(BarFrame(frames.Count, t, top));
            t += 1.0 / frameRate;
        }
        return (frames, topReachedAtS);
    }

    [Fact]
    public void A_re_rack_after_the_last_rep_does_not_change_its_mean_speed()
    {
        const double speedMps = 0.6;
        const double plateRadiusPx = 200.0;
        const double metersPerPixel = 0.225 / plateRadiusPx;
        const double top = 570.0;
        const double bottom = top + (speedMps * 0.6 / metersPerPixel);

        var (lastRepFrames, _) = OneCleanRep(0, top, bottom, speedMps, metersPerPixel, descendS: 1.0, concentricS: 0.6, holdS: 1.0);
        // After standing for the 1 s hold above, a slow further rise of 0.3 plate radii (the bar
        // being carried to the rack) with sideways drift, over another second.
        var reRackStart = lastRepFrames[^1].T + (1.0 / 30);
        for (var i = 0; i < 30; i++)
        {
            var t = reRackStart + (i / 30.0);
            var y = top - (0.3 * plateRadiusPx * (i / 29.0));
            var x = 10.0 * i; // sideways drift; irrelevant to the vertical speed, present for realism
            lastRepFrames.Add(new RawFrame { Index = lastRepFrames.Count, T = t, Bar = new RawBar { X = x, Y = y, RadiusPx = plateRadiusPx, Confidence = 0.9 }, Pose = null });
        }

        var (middleOfSetFrames, _) = OneCleanRep(0, top, bottom, speedMps, metersPerPixel, descendS: 1.0, concentricS: 0.6, holdS: 1.0);
        // In the middle of a set there is no re-rack: the next rep's own descent follows instead.
        // The segmenter's own window (unlike this direct helper call) would already stop the
        // first rep's search at the second rep's own bottom; capture that same boundary by hand.
        var firstRepSearchEnd = middleOfSetFrames.Count - 1;
        var nextDescentStart = middleOfSetFrames[^1].T + (1.0 / 30);
        var (nextRepFrames, _) = OneCleanRep(nextDescentStart, top, bottom, speedMps, metersPerPixel, descendS: 1.0, concentricS: 0.6, holdS: 0.5);
        foreach (var f in nextRepFrames)
        {
            middleOfSetFrames.Add(new RawFrame { Index = middleOfSetFrames.Count, T = f.T, Bar = f.Bar, Pose = f.Pose });
        }

        static double MeasureMean(List<RawFrame> frames, int searchEnd)
        {
            var bottomIndex = AnalysisEngine.RawTurningPointIndex(frames, 0, searchEnd, highest: true);
            var startLevelIndex = AnalysisEngine.RawTurningPointIndex(frames, 0, bottomIndex, highest: false);
            var startLevel = frames[startLevelIndex].Bar!.Y;
            var depthPx = frames[bottomIndex].Bar!.Y - startLevel;
            var lockout = AnalysisEngine.LockoutIndex(
                frames, bottomIndex, searchEnd, frames[bottomIndex].Bar!.Y, depthPx,
                AnalysisEngine.LockoutGateShare, AnalysisEngine.LockoutClimbToleranceShare, AnalysisEngine.LockoutClimbLookAheadS);
            var windowed = frames.Skip(bottomIndex).Take(lockout - bottomIndex + 1).Where(f => f.Bar is not null).ToList();
            return AnalysisEngine.MeanConcentricSpeedMps(windowed, metersPerPixel);
        }

        var reRackMean = MeasureMean(lastRepFrames, lastRepFrames.Count - 1);
        var middleMean = MeasureMean(middleOfSetFrames, firstRepSearchEnd);

        Assert.InRange(reRackMean, middleMean * 0.95, middleMean * 1.05);
        Assert.InRange(reRackMean, speedMps * 0.95, speedMps * 1.05);
    }

    [Fact]
    public void A_rep_finishing_3_percent_short_of_its_start_still_counts_as_reaching_the_top()
    {
        const double speedMps = 0.6;
        const double plateRadiusPx = 200.0;
        const double metersPerPixel = 0.225 / plateRadiusPx;
        const double top = 570.0;
        const double depthPx = speedMps * 0.6 / metersPerPixel;
        const double bottom = top + depthPx;
        // Just under the 3% tolerance (not exactly at it, to stay clear of floating-point
        // boundary flakiness on the <= comparison).
        const double finishShort = top + (0.029 * depthPx);

        var frames = new List<RawFrame>();
        var t = 0.0;
        for (var i = 0; i < 30; i++) // descend
        {
            t = i / 30.0;
            frames.Add(BarFrame(i, t, top + ((bottom - top) * (t / 1.0))));
        }
        var riseStart = t;
        for (var i = 0; i <= 18; i++) // rise, but only as far as finishShort, then hold there
        {
            t = riseStart + (i / 30.0);
            var target = Math.Max(finishShort, bottom - (speedMps * (t - riseStart) / metersPerPixel));
            frames.Add(BarFrame(frames.Count, t, target));
        }
        for (var i = 0; i < 15; i++) // holds at the slightly-short finish, nothing higher afterwards
        {
            t += 1.0 / 30;
            frames.Add(BarFrame(frames.Count, t, finishShort));
        }

        var bottomIndex = AnalysisEngine.RawTurningPointIndex(frames, 0, frames.Count - 1, highest: true);
        var startLevelIndex = AnalysisEngine.RawTurningPointIndex(frames, 0, bottomIndex, highest: false);
        var startLevel = frames[startLevelIndex].Bar!.Y;
        var measuredDepth = frames[bottomIndex].Bar!.Y - startLevel;
        var lockout = AnalysisEngine.LockoutIndex(
            frames, bottomIndex, frames.Count - 1, frames[bottomIndex].Bar!.Y, measuredDepth,
            AnalysisEngine.LockoutGateShare, AnalysisEngine.LockoutClimbToleranceShare, AnalysisEngine.LockoutClimbLookAheadS);

        // Lockout is accepted at the finish, not pushed out to "never" just because the bar
        // landed a little short of a full return.
        Assert.Equal(finishShort, frames[lockout].Bar!.Y, precision: 3);
    }

    [Fact]
    public void A_first_rep_preceded_by_an_unrack_at_a_different_height_uses_the_stance_not_the_unrack()
    {
        const double speedMps = 0.6;
        const double plateRadiusPx = 200.0;
        const double metersPerPixel = 0.225 / plateRadiusPx;
        const double standingTop = 570.0;
        const double unrackHeight = 650.0; // lower than standing: lifted up to standing before descending
        const double depthPx = speedMps * 0.6 / metersPerPixel;
        const double bottom = standingTop + depthPx;

        var frames = new List<RawFrame>();
        var t = 0.0;
        for (var i = 0; i < 10; i++) // brief unrack, lower than the true standing height
        {
            t = i / 30.0;
            frames.Add(BarFrame(i, t, unrackHeight));
        }
        var standStart = t;
        for (var i = 0; i < 15; i++) // rising to, then holding, the true standing height
        {
            t = standStart + (i / 30.0);
            frames.Add(BarFrame(frames.Count, t, standingTop));
        }
        var descendStart = t;
        for (var i = 0; i < 30; i++) // descend
        {
            t = descendStart + (i / 30.0);
            frames.Add(BarFrame(frames.Count, t, standingTop + ((bottom - standingTop) * (i / 29.0))));
        }
        var riseStart = t;
        for (var i = 0; i <= 18; i++) // rise back to standing at the known speed
        {
            t = riseStart + (i / 30.0);
            frames.Add(BarFrame(frames.Count, t, bottom - (speedMps * (t - riseStart) / metersPerPixel)));
        }

        var bottomIndex = AnalysisEngine.RawTurningPointIndex(frames, 0, frames.Count - 1, highest: true);
        var startLevelIndex = AnalysisEngine.RawTurningPointIndex(frames, 0, bottomIndex, highest: false);
        var startLevel = frames[startLevelIndex].Bar!.Y;

        // The unrack (650) is lower than the true standing height (570): the reference must be
        // the stance reached just before descending, not wherever the bar happened to be earlier.
        Assert.Equal(standingTop, startLevel, precision: 3);

        var lockout = AnalysisEngine.LockoutIndex(
            frames, bottomIndex, frames.Count - 1, frames[bottomIndex].Bar!.Y, depthPx,
            AnalysisEngine.LockoutGateShare, AnalysisEngine.LockoutClimbToleranceShare, AnalysisEngine.LockoutClimbLookAheadS);
        var windowed = frames.Skip(bottomIndex).Take(lockout - bottomIndex + 1).ToList();
        var mean = AnalysisEngine.MeanConcentricSpeedMps(windowed, metersPerPixel);
        Assert.InRange(mean, speedMps * 0.95, speedMps * 1.05);
    }

    [Fact]
    public void A_rep_that_tops_out_at_95_percent_and_stays_there_ends_at_the_top()
    {
        // The squat_10 shape: the lifter never gets all the way back to the pre-descent level,
        // settling a steady few percent short of it instead.
        const double plateRadiusPx = 200.0;
        const double metersPerPixel = 0.225 / plateRadiusPx;
        const double top = 570.0;
        const double depthPx = 330.0;
        const double bottom = top + depthPx;
        const double settledLevel = bottom - (0.95 * depthPx); // exactly the threshold
        const double riseDurationS = 1.0;

        var frames = new List<RawFrame>();
        for (var i = 0; i <= 30; i++) // rise from the bottom to the settled level over 1.0 s
        {
            var t = i / 30.0;
            frames.Add(BarFrame(i, t, bottom - ((bottom - settledLevel) * (t / riseDurationS))));
        }
        for (var i = 1; i <= 30; i++) // stays there for another 1.0 s; never reaches 100%
        {
            var t = riseDurationS + (i / 30.0);
            frames.Add(BarFrame(frames.Count, t, settledLevel));
        }

        var lockout = AnalysisEngine.LockoutIndex(
            frames, concentricStart: 0, outerEnd: frames.Count - 1, bottomY: bottom, depthPx: depthPx,
            AnalysisEngine.LockoutGateShare, AnalysisEngine.LockoutClimbToleranceShare, AnalysisEngine.LockoutClimbLookAheadS);

        Assert.InRange(frames[lockout].T, riseDurationS - 0.05, riseDurationS + 0.05);
        var windowed = frames.Take(lockout + 1).ToList();
        var mean = AnalysisEngine.MeanConcentricSpeedMps(windowed, metersPerPixel);
        var expectedMean = (0.95 * depthPx * metersPerPixel) / riseDurationS;
        Assert.InRange(mean, expectedMean * 0.95, expectedMean * 1.05);
    }

    [Fact]
    public void A_rep_that_reaches_96_percent_dips_then_creeps_to_100_over_0_8s_ends_at_the_96_percent_top()
    {
        // The squat_07 shape: the rise itself ends at 96%, then a brief dip (noise) is followed
        // by a slow settling creep, well outside the look-ahead window, up to 100%.
        const double top = 570.0;
        const double depthPx = 330.0;
        const double bottom = top + depthPx;
        const double topAt96 = bottom - (0.96 * depthPx);
        const double dipAt90 = bottom - (0.90 * depthPx);
        const double riseDurationS = 1.0;
        const double dipAtS = riseDurationS + 0.2; // 0.8 s creep after the dip, to t = riseDurationS + 1.0

        var frames = new List<RawFrame>();
        for (var i = 0; i <= 30; i++) // rise from the bottom to 96% over 1.0 s
        {
            var t = i / 30.0;
            frames.Add(BarFrame(i, t, bottom - ((bottom - topAt96) * (t / riseDurationS))));
        }
        for (var i = 1; i <= 6; i++) // dip to 90% over 0.2 s
        {
            var t = riseDurationS + (i / 30.0);
            frames.Add(BarFrame(frames.Count, t, topAt96 + ((dipAt90 - topAt96) * (i / 6.0))));
        }
        for (var i = 1; i <= 24; i++) // creep from 90% to 100% (the true top) over 0.8 s
        {
            var t = dipAtS + (i / 30.0);
            frames.Add(BarFrame(frames.Count, t, dipAt90 + ((top - dipAt90) * (i / 24.0))));
        }

        var lockout = AnalysisEngine.LockoutIndex(
            frames, concentricStart: 0, outerEnd: frames.Count - 1, bottomY: bottom, depthPx: depthPx,
            AnalysisEngine.LockoutGateShare, AnalysisEngine.LockoutClimbToleranceShare, AnalysisEngine.LockoutClimbLookAheadS);

        // Ends at the rise's own top (around t=1.0 s, recovered share at or just past the
        // threshold), not pulled on by the dip or by the later creep all the way to 100%.
        Assert.InRange(frames[lockout].T, riseDurationS - 0.1, riseDurationS + 0.05);
        var recoveredShare = (bottom - frames[lockout].Bar!.Y) / depthPx;
        Assert.InRange(recoveredShare, AnalysisEngine.LockoutGateShare, 0.97);
    }

    [Fact]
    public void A_fast_rep_crossing_95_percent_while_still_rising_ends_at_its_own_top_not_the_crossing()
    {
        // A continuous rise, still moving when it passes 95%, reaching its own top a further
        // 0.15 s later: the gate (0.85) only marks where the climb may be judged from, the climb
        // step itself must carry on to the real top.
        const double plateRadiusPx = 200.0;
        const double metersPerPixel = 0.225 / plateRadiusPx;
        const double top = 570.0;
        const double depthPx = 330.0;
        const double bottom = top + depthPx;
        const double riseDurationS = 3.0; // 95% at 2.85 s into the rise, the top at 3.0 s

        var frames = new List<RawFrame>();
        for (var i = 0; i <= 30; i++) // descend
        {
            var t = i / 30.0;
            frames.Add(BarFrame(i, t, top + ((bottom - top) * (t / 1.0))));
        }
        const double riseStart = 1.0;
        var riseFrameCount = (int)(riseDurationS * 30);
        for (var i = 1; i <= riseFrameCount; i++) // continuous rise, still moving right through 95%
        {
            var t = riseStart + (i / 30.0);
            frames.Add(BarFrame(frames.Count, t, bottom - ((bottom - top) * (i / (double)riseFrameCount))));
        }
        var topReachedAt = riseStart + riseDurationS;
        for (var i = 1; i <= 15; i++) // flat at the top afterward
        {
            var t = topReachedAt + (i / 30.0);
            frames.Add(BarFrame(frames.Count, t, top));
        }

        var bottomIndex = AnalysisEngine.RawTurningPointIndex(frames, 0, frames.Count - 1, highest: true);
        var lockout = AnalysisEngine.LockoutIndex(
            frames, bottomIndex, frames.Count - 1, frames[bottomIndex].Bar!.Y, depthPx,
            AnalysisEngine.LockoutGateShare, AnalysisEngine.LockoutClimbToleranceShare, AnalysisEngine.LockoutClimbLookAheadS);

        // Ends at the rise's own top (~4.0 s from the clip's start), not the 95% crossing
        // (~3.85 s) 0.15 s earlier.
        Assert.InRange(frames[lockout].T, topReachedAt - 0.07, topReachedAt + 0.07);

        var windowed = frames.Skip(bottomIndex).Take(lockout - bottomIndex + 1).ToList();
        var mean = AnalysisEngine.MeanConcentricSpeedMps(windowed, metersPerPixel);
        var expectedMean = depthPx * metersPerPixel / riseDurationS;
        Assert.InRange(mean, expectedMean * 0.97, expectedMean * 1.03);
    }

    [Fact]
    public void A_stall_at_60_percent_for_0_6s_does_not_end_the_rep()
    {
        const double top = 570.0;
        const double depthPx = 330.0;
        const double bottom = top + depthPx;
        const double stallLevel = bottom - (0.60 * depthPx);

        var frames = new List<RawFrame>();
        var t = 0.0;
        for (var i = 0; i <= 15; i++) // rise from the bottom to 60% over 0.5 s
        {
            t = i / 30.0;
            frames.Add(BarFrame(i, t, bottom - ((bottom - stallLevel) * (t / 0.5))));
        }
        var stallStart = t;
        for (var i = 1; i <= 18; i++) // stall at 60% for 0.6 s — well under the gate (85%)
        {
            t = stallStart + (i / 30.0);
            frames.Add(BarFrame(frames.Count, t, stallLevel));
        }
        var finishStart = t;
        for (var i = 1; i <= 18; i++) // finish: 60% to 100% over 0.6 s
        {
            t = finishStart + (i / 30.0);
            frames.Add(BarFrame(frames.Count, t, stallLevel - ((stallLevel - top) * (i / 18.0))));
        }
        var finishEnd = t;
        for (var i = 1; i <= 15; i++) // flat at the top afterward
        {
            t = finishEnd + (i / 30.0);
            frames.Add(BarFrame(frames.Count, t, top));
        }

        var bottomIndex = AnalysisEngine.RawTurningPointIndex(frames, 0, frames.Count - 1, highest: true);
        var lockout = AnalysisEngine.LockoutIndex(
            frames, bottomIndex, frames.Count - 1, frames[bottomIndex].Bar!.Y, depthPx,
            AnalysisEngine.LockoutGateShare, AnalysisEngine.LockoutClimbToleranceShare, AnalysisEngine.LockoutClimbLookAheadS);

        Assert.True(frames[lockout].T >= finishEnd - 0.07, $"stopped at t={frames[lockout].T}, inside the 60% stall rather than at the real finish (t={finishEnd})");
    }
}
