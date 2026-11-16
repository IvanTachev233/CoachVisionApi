using CoachVision.Analysis;

namespace CoachVision.Analysis.Tests;

/// <summary>Direct tests of the velocity window's own boundary detection (the engine's internal
/// helpers), separate from the end-to-end synthetic-clip tests: pins down behaviours a past
/// review found wrong (the start landing a frame early on an asymmetric turn, the end wandering
/// across a long noisy plateau, a stall mid-rise ending the window early, and a long still
/// stretch after lockout pulling the window on into it) so a future change can't reintroduce
/// any of them silently.</summary>
public sealed class VelocityWindowBoundaryTests
{
    private const double PlateRadiusPx = 200.0;

    private static RawFrame BarFrame(int index, double t, double? y) =>
        new() { Index = index, T = t, Bar = y is null ? null : new RawBar { X = 0, Y = y.Value, RadiusPx = PlateRadiusPx, Confidence = 0.9 }, Pose = null };

    [Fact]
    public void RawTurningPointIndex_finds_the_true_peak_even_with_an_asymmetric_turn()
    {
        // A slow rise (14.8 px/frame) into the bottom, then a much faster fall away from it:
        // a 3-wide average around the true peak (1014.44) ties with the point one frame early,
        // which is why the start search must not smooth.
        var frames = new[]
        {
            BarFrame(0, 0.000, 970.0),
            BarFrame(1, 0.033, 984.815),
            BarFrame(2, 0.067, 999.630),
            BarFrame(3, 0.100, 1014.444),
            BarFrame(4, 0.133, 984.815),
            BarFrame(5, 0.167, 955.185),
        };

        var index = AnalysisEngine.RawTurningPointIndex(frames, 0, 5, highest: true);

        Assert.Equal(3, index);
    }

    [Fact]
    public void RawTurningPointIndex_is_unaffected_by_frame_rate()
    {
        // The same turn, sampled twice as densely (60 fps): the bottom is still the single
        // frame with the highest raw value, found by direct comparison, not a frame count.
        var frames = new[]
        {
            BarFrame(0, 0.0000, 970.0),
            BarFrame(1, 0.0167, 977.4),
            BarFrame(2, 0.0333, 984.815),
            BarFrame(3, 0.0500, 992.2),
            BarFrame(4, 0.0667, 999.630),
            BarFrame(5, 0.0833, 1007.0),
            BarFrame(6, 0.1000, 1014.444),
            BarFrame(7, 0.1167, 999.6),
            BarFrame(8, 0.1333, 984.815),
            BarFrame(9, 0.1500, 970.0),
            BarFrame(10, 0.1667, 955.185),
        };

        var index = AnalysisEngine.RawTurningPointIndex(frames, 0, 10, highest: true);

        Assert.Equal(6, index);
    }

    [Fact]
    public void LockoutIndex_stops_near_the_first_plateau_not_the_noisy_global_minimum()
    {
        // Rises steadily to the lockout value (570) by frame 5, then wanders in a +/-3 px band
        // for a long stretch. The true lockout is around frame 5; a plain global-minimum search
        // over the whole window would instead land wherever the noise happens to dip lowest.
        var frames = new List<RawFrame>
        {
            BarFrame(0, 0.000, 900.0),
            BarFrame(1, 0.033, 800.0),
            BarFrame(2, 0.067, 700.0),
            BarFrame(3, 0.100, 630.0),
            BarFrame(4, 0.133, 590.0),
            BarFrame(5, 0.167, 570.0),
        };
        var noise = new[] { 1.0, -2.0, 0.5, -1.0, 2.0, -0.5, 1.5, -1.5, 0.0, 2.0, -2.0, 1.0, -0.5, 0.5, -1.0 };
        for (var i = 0; i < noise.Length; i++)
        {
            frames.Add(BarFrame(6 + i, 0.2 + (i * 0.033), 570.0 + noise[i]));
        }
        // One sample in the noisy tail dips lower than every genuine lockout reading.
        frames.Add(BarFrame(6 + noise.Length, 0.2 + (noise.Length * 0.033), 566.0));

        var end = AnalysisEngine.LockoutIndex(frames, concentricStart: 0, outerEnd: frames.Count - 1, bottomY: 900.0, depthPx: 330.0, AnalysisEngine.LockoutGateShare, AnalysisEngine.LockoutClimbToleranceShare, AnalysisEngine.LockoutClimbLookAheadS);

        // Nowhere near the stray low reading at the far end of the 16-frame noisy tail (index 21).
        Assert.InRange(end, 5, 8);
    }

    [Fact]
    public void LockoutIndex_stops_near_the_first_plateau_not_the_noisy_global_minimum_at_240fps()
    {
        // The same shape as the 30 fps version above, re-timed to 240 fps: the backward
        // correction this exercises is bounded by time (half of `SmoothingWindowS`), not a fixed
        // frame count, so it must land at the same real time regardless of how many raw frames
        // that time happens to span.
        const double frameRate = 240.0;
        const double riseDurationS = 0.167;
        var riseFrames = (int)(riseDurationS * frameRate);
        var frames = new List<RawFrame>();
        for (var i = 0; i <= riseFrames; i++)
        {
            var t = i / frameRate;
            var y = 900.0 - ((900.0 - 570.0) * (t / riseDurationS));
            frames.Add(BarFrame(i, t, y));
        }
        var noise = new[] { 1.0, -2.0, 0.5, -1.0, 2.0, -0.5, 1.5, -1.5, 0.0, 2.0, -2.0, 1.0, -0.5, 0.5, -1.0 };
        var plateauFrames = (int)(0.5 * frameRate);
        for (var i = 0; i < plateauFrames; i++)
        {
            var t = frames[^1].T + ((i + 1) / frameRate);
            frames.Add(BarFrame(frames.Count, t, 570.0 + noise[i % noise.Length]));
        }
        // One sample in the noisy tail dips lower than every genuine lockout reading.
        frames.Add(BarFrame(frames.Count, frames[^1].T + (1.0 / frameRate), 566.0));

        var end = AnalysisEngine.LockoutIndex(frames, concentricStart: 0, outerEnd: frames.Count - 1, bottomY: 900.0, depthPx: 330.0, AnalysisEngine.LockoutGateShare, AnalysisEngine.LockoutClimbToleranceShare, AnalysisEngine.LockoutClimbLookAheadS);

        // Nowhere near the stray low reading at the far end of the noisy tail, and landing within
        // the same real-time band the 30 fps version lands in (indices 5-8 there, at ~0.033 s/frame,
        // is t in [0.167, 0.267]).
        Assert.InRange(frames[end].T, riseDurationS - 0.02, riseDurationS + 0.10);
    }

    [Fact]
    public void LockoutIndex_skips_frames_with_no_bar()
    {
        var frames = new[]
        {
            BarFrame(0, 0.000, 900.0),
            BarFrame(1, 0.033, 800.0),
            BarFrame(2, 0.067, null),
            BarFrame(3, 0.100, 700.0),
            BarFrame(4, 0.133, 700.0),
            BarFrame(5, 0.167, 700.0),
            BarFrame(6, 0.200, 700.0),
        };

        var end = AnalysisEngine.LockoutIndex(frames, concentricStart: 0, outerEnd: 6, bottomY: 900.0, depthPx: 200.0, AnalysisEngine.LockoutGateShare, AnalysisEngine.LockoutClimbToleranceShare, AnalysisEngine.LockoutClimbLookAheadS);

        Assert.Equal(3, end);
    }

    [Fact]
    public void LockoutIndex_does_not_stop_at_a_mid_rise_stall()
    {
        // Rises, stalls at 760 for a while (not the eventual target of 570), then finishes.
        var frames = new List<RawFrame>
        {
            BarFrame(0, 0.00, 900.0),
            BarFrame(1, 0.10, 800.0),
        };
        for (var i = 0; i < 18; i++)
        {
            frames.Add(BarFrame(2 + i, 0.20 + (i * 0.033), 760.0)); // ~0.6 s stall
        }
        frames.Add(BarFrame(20, 0.80, 700.0));
        frames.Add(BarFrame(21, 0.90, 630.0));
        frames.Add(BarFrame(22, 1.00, 570.0));
        frames.Add(BarFrame(23, 1.10, 570.0));

        var end = AnalysisEngine.LockoutIndex(frames, concentricStart: 0, outerEnd: 23, bottomY: 900.0, depthPx: 330.0, AnalysisEngine.LockoutGateShare, AnalysisEngine.LockoutClimbToleranceShare, AnalysisEngine.LockoutClimbLookAheadS);

        Assert.True(end >= 22, $"stopped at {end}, inside the stall rather than at the real finish");
    }

    [Fact]
    public void LockoutIndex_does_not_run_on_into_a_long_still_stretch_after_lockout()
    {
        var frames = new List<RawFrame>
        {
            BarFrame(0, 0.00, 900.0),
            BarFrame(1, 0.10, 800.0),
            BarFrame(2, 0.20, 700.0),
            BarFrame(3, 0.30, 630.0),
            BarFrame(4, 0.40, 570.0),
        };
        for (var i = 0; i < 90; i++) // 3 s motionless after lockout
        {
            frames.Add(BarFrame(5 + i, 0.50 + (i * 0.033), 570.0));
        }

        var end = AnalysisEngine.LockoutIndex(frames, concentricStart: 0, outerEnd: frames.Count - 1, bottomY: 900.0, depthPx: 330.0, AnalysisEngine.LockoutGateShare, AnalysisEngine.LockoutClimbToleranceShare, AnalysisEngine.LockoutClimbLookAheadS);

        Assert.InRange(end, 4, 6);
    }
}
