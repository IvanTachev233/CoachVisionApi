namespace CoachVision.Analysis.Internal;

/// <summary>The smoothing a rep counter needs over a per-frame track: fill the gaps between the
/// first and last found frame in a straight line, run a median filter over 5 frames, then a
/// moving average over 5 frames. Ported by hand from the reference Python implementation that
/// chose this filtering, since there is no SciPy in .NET.</summary>
public static class Smoothing
{
    /// <summary>Linear interpolation of `values` at `knownIndices` (ascending, at least one entry)
    /// onto every index from the first to the last known index. `times` gives the x-axis; equal or
    /// non-increasing neighbouring times are treated as the same point (no divide by zero).</summary>
    public static double[] FillGaps(double[] times, double[] values, int[] knownIndices)
    {
        var first = knownIndices[0];
        var last = knownIndices[^1];
        var length = last - first + 1;
        var filled = new double[length];
        var k = 0;
        for (var i = 0; i < length; i++)
        {
            var index = first + i;
            while (k + 1 < knownIndices.Length && knownIndices[k + 1] <= index)
            {
                k++;
            }
            var leftIndex = knownIndices[k];
            if (index <= leftIndex || k + 1 >= knownIndices.Length)
            {
                filled[i] = values[leftIndex];
                continue;
            }
            var rightIndex = knownIndices[k + 1];
            if (index >= rightIndex)
            {
                filled[i] = values[rightIndex];
                continue;
            }
            var leftT = times[leftIndex];
            var rightT = times[rightIndex];
            var span = rightT - leftT;
            var fraction = span > 0 ? (times[index] - leftT) / span : 0.0;
            filled[i] = values[leftIndex] + (values[rightIndex] - values[leftIndex]) * fraction;
        }
        return filled;
    }

    /// <summary>A median filter over a window of `width` samples (odd), zero-padded past each
    /// end exactly as `scipy.signal.medfilt` does (not the signal's own edge value): a one-frame
    /// outlier right at the first or last tracked frame must not survive by being counted several
    /// times over.</summary>
    public static double[] MedianFilter(double[] values, int width)
    {
        var half = width / 2;
        var output = new double[values.Length];
        var window = new double[width];
        for (var i = 0; i < values.Length; i++)
        {
            for (var w = 0; w < width; w++)
            {
                var index = i - half + w;
                window[w] = index >= 0 && index < values.Length ? values[index] : 0.0;
            }
            Array.Sort(window);
            output[i] = window[width / 2];
        }
        return output;
    }

    /// <summary>A moving average over a window of `width` samples, with the signal's edge value
    /// repeated past each end (as `np.pad(..., mode="edge")` does before `np.convolve`).</summary>
    public static double[] MovingAverage(double[] values, int width)
    {
        var half = width / 2;
        var output = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            double sum = 0;
            for (var w = -half; w <= half; w++)
            {
                var index = Math.Clamp(i + w, 0, values.Length - 1);
                sum += values[index];
            }
            output[i] = sum / width;
        }
        return output;
    }
}
