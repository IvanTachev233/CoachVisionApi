using CoachVision.Analysis.Internal;

namespace CoachVision.Analysis.Tests;

public sealed class SmoothingTests
{
    [Fact]
    public void FillGaps_interpolates_linearly_between_known_points()
    {
        var times = new double[] { 0, 1, 2, 3, 4 };
        var values = new double[] { 10, 0, 0, 0, 20 };
        var filled = Smoothing.FillGaps(times, values, new[] { 0, 4 });

        Assert.Equal(new double[] { 10, 12.5, 15, 17.5, 20 }, filled);
    }

    [Fact]
    public void FillGaps_leaves_known_points_exact_and_fills_an_uneven_gap()
    {
        var times = new double[] { 0, 1, 3, 4 };
        var values = new double[] { 0, 0, 0, 12 };
        var filled = Smoothing.FillGaps(times, values, new[] { 0, 3 });

        Assert.Equal(0, filled[0]);
        Assert.Equal(9, filled[2], precision: 10);
    }

    [Fact]
    public void MedianFilter_removes_a_single_sample_spike()
    {
        var values = new double[] { 1, 1, 1, 100, 1, 1, 1 };
        var filtered = Smoothing.MedianFilter(values, 5);

        Assert.All(filtered, v => Assert.Equal(1, v));
    }

    [Fact]
    public void MedianFilter_zero_pads_past_each_end_matching_scipy_medfilt()
    {
        // scipy.signal.medfilt([8,2,2,2,2,10,10,10,2,2,2,2], 5) == [2,2,2,2,2,10,10,10,2,2,2,2]:
        // at index 0 the window is [0,0,8,2,2] (zero-padded), median 2, not the repeated-edge
        // window [8,8,8,2,2] (median 8) a one-frame outlier at the very start would survive on.
        var values = new double[] { 8, 2, 2, 2, 2, 10, 10, 10, 2, 2, 2, 2 };
        var filtered = Smoothing.MedianFilter(values, 5);

        Assert.Equal(new double[] { 2, 2, 2, 2, 2, 10, 10, 10, 2, 2, 2, 2 }, filtered);
    }

    [Fact]
    public void MedianFilter_zero_pads_a_short_array_whose_edge_differs_from_its_neighbours()
    {
        // Window at index 0 is [0,0,9,1,1] (zero-padded): sorted [0,0,1,1,9], median 1.
        var values = new double[] { 9, 1, 1, 1, 1 };
        var filtered = Smoothing.MedianFilter(values, 5);

        Assert.Equal(1, filtered[0]);
    }

    [Fact]
    public void MovingAverage_of_a_constant_series_is_unchanged()
    {
        var values = Enumerable.Repeat(3.0, 10).ToArray();
        var averaged = Smoothing.MovingAverage(values, 5);

        Assert.All(averaged, v => Assert.Equal(3.0, v));
    }

    [Fact]
    public void MovingAverage_smooths_a_single_sample_jitter()
    {
        var values = new double[] { 10, 10, 10, 10, 50, 10, 10, 10, 10 };
        var averaged = Smoothing.MovingAverage(values, 5);

        Assert.True(averaged[4] < 50);
        Assert.True(averaged[4] > 10);
    }
}
