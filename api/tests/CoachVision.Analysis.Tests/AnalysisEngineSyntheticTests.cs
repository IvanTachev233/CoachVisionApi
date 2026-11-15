using CoachVision.Analysis;

namespace CoachVision.Analysis.Tests;

public sealed class AnalysisEngineSyntheticTests
{
    [Fact]
    public void Clean_reps_are_counted_from_the_shoulders()
    {
        var raw = SyntheticClipBuilder.Build(frameCount: 300, shoulderYOf: SyntheticClipBuilder.Reps(count: 4, repSeconds: 2.5, amplitudePx: 250));
        var request = SyntheticClipBuilder.SquatRequest(new[] { Feature.RepCount });

        var results = AnalysisEngine.Analyze(raw, request);

        var repCount = Assert.IsType<RepCountAnalysed>(results.RepCount);
        Assert.Equal(4, repCount.Count);
        Assert.Equal(4, repCount.Reps.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, repCount.Reps.Select(r => r.Rep));
    }

    [Fact]
    public void A_paused_rep_still_counts_as_one()
    {
        static double Paused(double t)
        {
            const double repSeconds = 2.5;
            const double pauseStart = 1.0;
            const double pauseLength = 0.6;
            if (t >= pauseStart && t < pauseStart + pauseLength)
            {
                t = pauseStart;
            }
            else if (t >= pauseStart + pauseLength)
            {
                t -= pauseLength;
            }
            return SyntheticClipBuilder.Reps(2, repSeconds, 250)(t);
        }

        var raw = SyntheticClipBuilder.Build(frameCount: 200, shoulderYOf: Paused);
        var request = SyntheticClipBuilder.SquatRequest(new[] { Feature.RepCount });

        var results = AnalysisEngine.Analyze(raw, request);

        var repCount = Assert.IsType<RepCountAnalysed>(results.RepCount);
        Assert.Equal(2, repCount.Count);
    }

    [Fact]
    public void A_walkout_dip_smaller_than_depth_is_not_a_rep()
    {
        static double WithWalkout(double t)
        {
            var baseline = SyntheticClipBuilder.StandingShoulderY;
            if (t < 0.6)
            {
                // A shallow dip well under the 0.5-torso depth needed to count (150 px here).
                return baseline + (40 * Math.Sin(Math.PI * t / 0.6));
            }
            return SyntheticClipBuilder.Reps(3, 2.5, 250)(t - 0.6);
        }

        var raw = SyntheticClipBuilder.Build(frameCount: 240, shoulderYOf: WithWalkout);
        var request = SyntheticClipBuilder.SquatRequest(new[] { Feature.RepCount });

        var results = AnalysisEngine.Analyze(raw, request);

        var repCount = Assert.IsType<RepCountAnalysed>(results.RepCount);
        Assert.Equal(3, repCount.Count);
    }

    [Fact]
    public void No_plate_gives_velocity_and_bar_path_unanalysable_but_rep_count_still_analysed()
    {
        var raw = SyntheticClipBuilder.Build(
            frameCount: 250,
            shoulderYOf: SyntheticClipBuilder.Reps(3, 2.5, 250),
            includeBar: false);
        var request = SyntheticClipBuilder.SquatRequest(SyntheticClipBuilder.AllFeatures);

        var results = AnalysisEngine.Analyze(raw, request);

        var velocity = Assert.IsAssignableFrom<IUnanalysable>(results.Velocity);
        Assert.Equal(ReasonCodes.PlatesNotFound, velocity.Reason);
        var barPath = Assert.IsAssignableFrom<IUnanalysable>(results.BarPath);
        Assert.Equal(ReasonCodes.PlatesNotFound, barPath.Reason);
        var repCount = Assert.IsType<RepCountAnalysed>(results.RepCount);
        Assert.Equal(3, repCount.Count);
    }

    [Fact]
    public void A_bar_that_never_moves_while_the_shoulders_do_four_reps_is_a_mismatch()
    {
        var raw = SyntheticClipBuilder.Build(
            frameCount: 300,
            shoulderYOf: SyntheticClipBuilder.Reps(4, 2.5, 250),
            barYOf: _ => SyntheticClipBuilder.StandingShoulderY - SyntheticClipBuilder.BarAboveShoulders); // racked, never moves
        var request = SyntheticClipBuilder.SquatRequest(SyntheticClipBuilder.AllFeatures);

        var results = AnalysisEngine.Analyze(raw, request);

        var velocity = Assert.IsAssignableFrom<IUnanalysable>(results.Velocity);
        Assert.Equal(ReasonCodes.BarTrackMismatch, velocity.Reason);
        var barPath = Assert.IsAssignableFrom<IUnanalysable>(results.BarPath);
        Assert.Equal(ReasonCodes.BarTrackMismatch, barPath.Reason);
        var repCount = Assert.IsType<RepCountAnalysed>(results.RepCount);
        Assert.Equal(4, repCount.Count);
    }

    [Fact]
    public void A_handful_of_implausible_radius_readings_are_treated_as_no_bar()
    {
        // A detector glitch: five frames report a radius a tenth the size of every other frame
        // in the clip (a shadow or some other small object caught instead of the plate).
        var glitchFrames = new HashSet<int> { 40, 41, 80, 81, 150 };
        var raw = SyntheticClipBuilder.Build(
            frameCount: 300,
            shoulderYOf: SyntheticClipBuilder.Reps(4, 2.5, 250),
            radiusOf: t =>
            {
                var index = (int)Math.Round(t * SyntheticClipBuilder.FrameRate);
                return glitchFrames.Contains(index) ? 20.0 : SyntheticClipBuilder.PlateRadiusPx;
            });
        var request = SyntheticClipBuilder.SquatRequest(new[] { Feature.BarPath, Feature.Velocity, Feature.RepCount });

        var results = AnalysisEngine.Analyze(raw, request);

        var barPath = Assert.IsType<BarPathAnalysed>(results.BarPath);
        // 300 frames minus the 5 glitched ones: none of those five show up as a sample.
        Assert.Equal(295, barPath.Samples.Count);
        var velocity = Assert.IsType<VelocityAnalysed>(results.Velocity);
        Assert.Equal(4, velocity.Reps.Count);
    }

    [Fact]
    public void A_non_squat_lift_is_unanalysable_for_every_requested_feature()
    {
        var raw = SyntheticClipBuilder.Build(frameCount: 150, shoulderYOf: SyntheticClipBuilder.Reps(3, 2.5, 250));
        var request = new AnalysisRequest
        {
            Lift = Lift.Deadlift,
            Features = SyntheticClipBuilder.AllFeatures,
            PlateDiameterMm = 450,
            PlaybackWidth = 1080,
            PlaybackHeight = 1920,
        };

        var results = AnalysisEngine.Analyze(raw, request);

        Assert.Equal(ReasonCodes.LiftNotSupported, Assert.IsAssignableFrom<IUnanalysable>(results.BarPath).Reason);
        Assert.Equal(ReasonCodes.LiftNotSupported, Assert.IsAssignableFrom<IUnanalysable>(results.Velocity).Reason);
        Assert.Equal(ReasonCodes.LiftNotSupported, Assert.IsAssignableFrom<IUnanalysable>(results.RepCount).Reason);
        Assert.Equal(ReasonCodes.LiftNotSupported, Assert.IsAssignableFrom<IUnanalysable>(results.VerificationChecks!.SquatDepth).Reason);
    }

    [Fact]
    public void Missing_pose_throughout_gives_pose_not_found()
    {
        var raw = SyntheticClipBuilder.Build(frameCount: 150, shoulderYOf: SyntheticClipBuilder.Reps(3, 2.5, 250), includePose: false);
        var request = SyntheticClipBuilder.SquatRequest(new[] { Feature.RepCount, Feature.VerificationChecks });

        var results = AnalysisEngine.Analyze(raw, request);

        Assert.Equal(ReasonCodes.PoseNotFound, Assert.IsAssignableFrom<IUnanalysable>(results.RepCount).Reason);
        Assert.Equal(ReasonCodes.PoseNotFound, Assert.IsAssignableFrom<IUnanalysable>(results.VerificationChecks!.SquatDepth).Reason);
    }

    [Fact]
    public void No_reps_at_all_gives_no_reps_detected_rather_than_a_zero_count()
    {
        var raw = SyntheticClipBuilder.Build(frameCount: 150, shoulderYOf: _ => SyntheticClipBuilder.StandingShoulderY);
        var request = SyntheticClipBuilder.SquatRequest(new[] { Feature.RepCount });

        var results = AnalysisEngine.Analyze(raw, request);

        Assert.Equal(ReasonCodes.NoRepsDetected, Assert.IsAssignableFrom<IUnanalysable>(results.RepCount).Reason);
    }

    [Fact]
    public void A_clean_known_constant_bar_speed_is_within_5_percent()
    {
        const double speedMps = 1.0;
        var reps = new[] { new SyntheticClipBuilder.RepSpec(DescendS: 1.0, BottomHoldS: 0.0, ConcentricS: 0.6, SpeedMps: speedMps, RestS: 1.0) };
        var raw = SyntheticClipBuilder.Build(
            frameCount: 90,
            shoulderYOf: SyntheticClipBuilder.Reps(1, 2.0, 250),
            barYOf: SyntheticClipBuilder.ConcentricBarTrack(reps));
        var request = SyntheticClipBuilder.SquatRequest(new[] { Feature.Velocity, Feature.RepCount });

        var results = AnalysisEngine.Analyze(raw, request);

        var repCount = Assert.IsType<RepCountAnalysed>(results.RepCount);
        Assert.Equal(1, repCount.Count);
        var velocity = Assert.IsType<VelocityAnalysed>(results.Velocity);
        Assert.Single(velocity.Reps);
        Assert.InRange(velocity.Reps[0].MeanConcentricMps, speedMps * 0.95, speedMps * 1.05);
        Assert.InRange(velocity.Reps[0].PeakConcentricMps, speedMps * 0.95, speedMps * 1.05);
    }

    [Fact]
    public void A_known_speed_survives_pixel_jitter_and_missing_frames_within_15_percent()
    {
        const double speedMps = 0.6;
        var reps = new[] { new SyntheticClipBuilder.RepSpec(DescendS: 1.0, BottomHoldS: 0.0, ConcentricS: 0.8, SpeedMps: speedMps, RestS: 1.0) };
        var clean = SyntheticClipBuilder.ConcentricBarTrack(reps);
        var jitterRng = new Random(20261115);
        double? Jittered(double t)
        {
            var y = clean(t);
            // +/- 3 px of detector noise, and every 7th frame has no bar at all (a brief miss).
            return y is null || (int)Math.Round(t * SyntheticClipBuilder.FrameRate) % 7 == 0
                ? null
                : y + ((jitterRng.NextDouble() * 6) - 3);
        }

        var raw = SyntheticClipBuilder.Build(
            frameCount: 90,
            shoulderYOf: SyntheticClipBuilder.Reps(1, 2.0, 250),
            barYOf: Jittered,
            radiusOf: _ => SyntheticClipBuilder.PlateRadiusPx + ((jitterRng.NextDouble() * 4) - 2));
        var request = SyntheticClipBuilder.SquatRequest(new[] { Feature.Velocity, Feature.RepCount });

        var results = AnalysisEngine.Analyze(raw, request);

        var velocity = Assert.IsType<VelocityAnalysed>(results.Velocity);
        Assert.Single(velocity.Reps);
        Assert.InRange(velocity.Reps[0].MeanConcentricMps, speedMps * 0.85, speedMps * 1.15);
    }

    [Fact]
    public void Two_reps_at_different_speeds_each_get_their_own_speed()
    {
        var reps = new[]
        {
            new SyntheticClipBuilder.RepSpec(DescendS: 1.0, BottomHoldS: 0.0, ConcentricS: 1.2, SpeedMps: 0.4, RestS: 1.0),
            new SyntheticClipBuilder.RepSpec(DescendS: 1.0, BottomHoldS: 0.0, ConcentricS: 0.5, SpeedMps: 1.0, RestS: 1.0),
        };
        var raw = SyntheticClipBuilder.Build(
            frameCount: 240,
            shoulderYOf: SyntheticClipBuilder.Reps(2, 3.2, 250),
            barYOf: SyntheticClipBuilder.ConcentricBarTrack(reps));
        var request = SyntheticClipBuilder.SquatRequest(new[] { Feature.Velocity, Feature.RepCount });

        var results = AnalysisEngine.Analyze(raw, request);

        var repCount = Assert.IsType<RepCountAnalysed>(results.RepCount);
        Assert.Equal(2, repCount.Count);
        var velocity = Assert.IsType<VelocityAnalysed>(results.Velocity);
        Assert.Equal(2, velocity.Reps.Count);
        Assert.InRange(velocity.Reps[0].MeanConcentricMps, 0.4 * 0.95, 0.4 * 1.05);
        Assert.InRange(velocity.Reps[1].MeanConcentricMps, 1.0 * 0.95, 1.0 * 1.05);
    }

    [Fact]
    public void A_different_plate_diameter_scales_the_result_exactly()
    {
        const double speedMps = 0.5;
        var reps = new[] { new SyntheticClipBuilder.RepSpec(DescendS: 1.0, BottomHoldS: 0.0, ConcentricS: 0.7, SpeedMps: speedMps, RestS: 1.0) };
        // The track is built assuming a 450 mm plate; declaring 400 mm instead must scale the
        // measured speed by exactly 400 / 450, since the same pixel motion now means less metal.
        var raw = SyntheticClipBuilder.Build(
            frameCount: 90,
            shoulderYOf: SyntheticClipBuilder.Reps(1, 2.0, 250),
            barYOf: SyntheticClipBuilder.ConcentricBarTrack(reps, plateDiameterMm: 450));
        var requestAt450 = SyntheticClipBuilder.SquatRequest(new[] { Feature.Velocity }, plateDiameterMm: 450);
        var requestAt400 = SyntheticClipBuilder.SquatRequest(new[] { Feature.Velocity }, plateDiameterMm: 400);

        var at450 = Assert.IsType<VelocityAnalysed>(AnalysisEngine.Analyze(raw, requestAt450).Velocity);
        var at400 = Assert.IsType<VelocityAnalysed>(AnalysisEngine.Analyze(raw, requestAt400).Velocity);

        Assert.InRange(at450.Reps[0].MeanConcentricMps, speedMps * 0.95, speedMps * 1.05);
        var expectedAt400 = at450.Reps[0].MeanConcentricMps * (400.0 / 450.0);
        Assert.Equal(expectedAt400, at400.Reps[0].MeanConcentricMps, precision: 9);
    }

    [Fact]
    public void One_reps_bar_missing_from_its_whole_concentric_window_withholds_velocity_but_not_rep_count()
    {
        // Four reps, clip-wide bar coverage well above the clip-level gate (80%): only the third
        // rep's own rise has no bar at all. A made-up zero for that one rep is not an option
        // (there is nothing to compute a speed from), and `RepVelocity` has no partial or
        // per-rep unanalysable shape, so the whole feature is withheld instead.
        const double repSeconds = 2.6;
        var reps = Enumerable.Range(0, 4)
            .Select(_ => new SyntheticClipBuilder.RepSpec(DescendS: 1.0, BottomHoldS: 0.0, ConcentricS: 0.6, SpeedMps: 0.5, RestS: 1.0))
            .ToArray();
        var clean = SyntheticClipBuilder.ConcentricBarTrack(reps);
        const double thirdRepConcentricStart = (2 * repSeconds) + 1.0;
        const double thirdRepConcentricEnd = thirdRepConcentricStart + 0.6;
        double? WithoutThirdRepsRise(double t) => t >= thirdRepConcentricStart && t < thirdRepConcentricEnd ? null : clean(t);

        var raw = SyntheticClipBuilder.Build(
            frameCount: (int)(4 * repSeconds * SyntheticClipBuilder.FrameRate),
            shoulderYOf: SyntheticClipBuilder.Reps(4, repSeconds, 250),
            barYOf: WithoutThirdRepsRise);
        var request = SyntheticClipBuilder.SquatRequest(new[] { Feature.Velocity, Feature.RepCount });

        var results = AnalysisEngine.Analyze(raw, request);

        var repCount = Assert.IsType<RepCountAnalysed>(results.RepCount);
        Assert.Equal(4, repCount.Count);
        var velocity = Assert.IsAssignableFrom<IUnanalysable>(results.Velocity);
        Assert.Equal(ReasonCodes.PlatesNotFound, velocity.Reason);
    }

    [Fact]
    public void A_rep_with_a_gap_mid_rise_but_samples_either_side_is_still_measured_normally()
    {
        const double speedMps = 0.6;
        var reps = new[] { new SyntheticClipBuilder.RepSpec(DescendS: 1.0, BottomHoldS: 0.0, ConcentricS: 0.9, SpeedMps: speedMps, RestS: 1.0) };
        var clean = SyntheticClipBuilder.ConcentricBarTrack(reps);
        // The rise runs from t=1.0 to t=1.9; a 0.3 s gap sits in the middle of it, with solid
        // samples both before and after.
        const double gapStart = 1.3;
        const double gapEnd = 1.6;
        double? WithMidRiseGap(double t) => t >= gapStart && t < gapEnd ? null : clean(t);

        var raw = SyntheticClipBuilder.Build(
            frameCount: 90,
            shoulderYOf: SyntheticClipBuilder.Reps(1, 2.0, 250),
            barYOf: WithMidRiseGap);
        var request = SyntheticClipBuilder.SquatRequest(new[] { Feature.Velocity, Feature.RepCount });

        var results = AnalysisEngine.Analyze(raw, request);

        var velocity = Assert.IsType<VelocityAnalysed>(results.Velocity);
        Assert.Single(velocity.Reps);
        Assert.InRange(velocity.Reps[0].MeanConcentricMps, speedMps * 0.9, speedMps * 1.1);
    }

    [Fact]
    public void An_implausibly_high_peak_withholds_velocity_as_low_confidence_instead_of_capping_it()
    {
        // One clean rep, then a single rogue frame mid-rise jumps several hundred pixels in a
        // couple of milliseconds — the kind of detector glitch (jumping to a different object)
        // that produces a peak no real lift could reach.
        const double speedMps = 0.6;
        var reps = new[] { new SyntheticClipBuilder.RepSpec(DescendS: 1.0, BottomHoldS: 0.0, ConcentricS: 0.6, SpeedMps: speedMps, RestS: 1.0) };
        var clean = SyntheticClipBuilder.ConcentricBarTrack(reps);
        var glitchFrameIndex = (int)(1.3 * SyntheticClipBuilder.FrameRate);
        double? Glitched(double t)
        {
            var y = clean(t);
            return y is null || (int)Math.Round(t * SyntheticClipBuilder.FrameRate) != glitchFrameIndex ? y : y.Value - 600.0;
        }

        var raw = SyntheticClipBuilder.Build(
            frameCount: 90,
            shoulderYOf: SyntheticClipBuilder.Reps(1, 2.0, 250),
            barYOf: Glitched);
        var request = SyntheticClipBuilder.SquatRequest(new[] { Feature.Velocity });

        var velocity = Assert.IsAssignableFrom<IUnanalysable>(AnalysisEngine.Analyze(raw, request).Velocity);

        Assert.Equal(ReasonCodes.LowConfidence, velocity.Reason);
    }

    [Fact]
    public void BarPath_px_py_are_clamped_to_the_playback_frame_even_when_the_raw_centre_falls_outside_it()
    {
        // The raw schema allows a circle whose centre sits just outside the frame (its edge can
        // still be in view); the contract's own pixel coordinates do not — they describe a point
        // drawn on the playback image, which has to stay on it.
        var reps = new[] { new SyntheticClipBuilder.RepSpec(DescendS: 1.0, BottomHoldS: 0.0, ConcentricS: 0.6, SpeedMps: 0.6, RestS: 1.0) };
        var raw = SyntheticClipBuilder.Build(
            frameCount: 90,
            shoulderYOf: SyntheticClipBuilder.Reps(1, 2.0, 250),
            barYOf: SyntheticClipBuilder.ConcentricBarTrack(reps));
        var frames = raw.Frames.ToList();
        var offLeft = frames[10];
        frames[10] = new RawFrame { Index = offLeft.Index, T = offLeft.T, Bar = new RawBar { X = -2.3, Y = offLeft.Bar!.Y, RadiusPx = offLeft.Bar!.RadiusPx, Confidence = offLeft.Bar!.Confidence }, Pose = offLeft.Pose };
        var offRight = frames[20];
        var pastRightEdge = raw.Video.Width + 5.0;
        frames[20] = new RawFrame { Index = offRight.Index, T = offRight.T, Bar = new RawBar { X = pastRightEdge, Y = offRight.Bar!.Y, RadiusPx = offRight.Bar!.RadiusPx, Confidence = offRight.Bar!.Confidence }, Pose = offRight.Pose };
        var patched = new RawOutputDocument { SchemaVersion = raw.SchemaVersion, JobId = raw.JobId, Worker = raw.Worker, Video = raw.Video, Frames = frames };
        var request = SyntheticClipBuilder.SquatRequest(new[] { Feature.BarPath });

        var barPath = Assert.IsType<BarPathAnalysed>(AnalysisEngine.Analyze(patched, request).BarPath);

        var offLeftSample = barPath.Samples.Single(s => s.T == offLeft.T);
        var offRightSample = barPath.Samples.Single(s => s.T == offRight.T);
        Assert.Equal(0, offLeftSample.Px);
        Assert.Equal((double)request.PlaybackWidth, offRightSample.Px);
        // Metres (the measured, not the drawn, coordinate) stay unclamped.
        Assert.NotEqual(0, offLeftSample.X);
    }
}
