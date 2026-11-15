using CoachVision.Analysis.Internal;

namespace CoachVision.Analysis.Tests;

public sealed class PeakFinderTests
{
    [Fact]
    public void FindPeaks_finds_one_peak_per_hump()
    {
        var values = Wave(new[] { 0.0, 10.0, 0.0, 10.0, 0.0, 10.0, 0.0 }, framesPerSegment: 5);

        var peaks = PeakFinder.FindPeaks(values, minProminence: 5, distance: 1);

        Assert.Equal(3, peaks.Count);
    }

    [Fact]
    public void FindPeaks_drops_a_peak_below_the_prominence_threshold()
    {
        // A small dip too shallow to count (half the depth of the real reps either side).
        var values = Wave(new[] { 0.0, 10.0, 0.0, 3.0, 0.0, 10.0, 0.0 }, framesPerSegment: 5);

        var peaks = PeakFinder.FindPeaks(values, minProminence: 5, distance: 1);

        Assert.Equal(2, peaks.Count);
    }

    [Fact]
    public void FindPeaks_merges_peaks_closer_than_the_minimum_distance()
    {
        var values = new double[] { 0, 1, 10, 1, 0, 1, 9, 1, 0 };

        var peaks = PeakFinder.FindPeaks(values, minProminence: 5, distance: 6);

        Assert.Single(peaks);
        Assert.Equal(2, peaks[0].Index); // the higher of the two close peaks is kept
    }

    [Fact]
    public void FindPeaks_breaks_a_tie_between_equal_peaks_by_keeping_the_later_one()
    {
        // Two peaks of exactly the same height, closer together than `distance`: SciPy's own
        // distance filter keeps the higher-index one of an exact tie, not the first found.
        var values = new double[] { 0, 10, 0, 10, 0 };

        var peaks = PeakFinder.FindPeaks(values, minProminence: 5, distance: 3);

        Assert.Single(peaks);
        Assert.Equal(3, peaks[0].Index);
    }

    [Fact]
    public void FindPeaks_takes_the_midpoint_of_a_flat_plateau()
    {
        var values = new double[] { 0, 5, 10, 10, 10, 5, 0 };

        var peaks = PeakFinder.FindPeaks(values, minProminence: 5, distance: 1);

        Assert.Single(peaks);
        Assert.Equal(3, peaks[0].Index);
    }

    [Fact]
    public void FindPeaks_returns_nothing_for_a_flat_signal()
    {
        var values = Enumerable.Repeat(5.0, 20).ToArray();

        var peaks = PeakFinder.FindPeaks(values, minProminence: 1, distance: 1);

        Assert.Empty(peaks);
    }

    /// <summary>Linear ramps between each value in `corners`, `framesPerSegment` long.</summary>
    private static double[] Wave(double[] corners, int framesPerSegment)
    {
        var values = new List<double>();
        for (var i = 0; i < corners.Length - 1; i++)
        {
            for (var f = 0; f < framesPerSegment; f++)
            {
                values.Add(corners[i] + (corners[i + 1] - corners[i]) * f / framesPerSegment);
            }
        }
        values.Add(corners[^1]);
        return values.ToArray();
    }
}
