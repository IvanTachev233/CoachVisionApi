namespace CoachVision.Analysis.Internal;

/// <summary>A peak with its prominence, in the smoothed signal's own index space.</summary>
public readonly record struct Peak(int Index, double Value, double Prominence);

/// <summary>A by-hand port of the two `scipy.signal.find_peaks` keywords the reference rep
/// counter uses (`prominence=`, `distance=`): there is no SciPy in .NET. Finds local maxima (a rep's
/// bottom, since the tracks here are in pixels with larger meaning lower), keeps the ones whose
/// prominence reaches the given minimum, then thins any that are closer together than `distance`
/// samples by keeping the higher of each close pair, exactly as SciPy's `distance` filter does
/// (highest peaks first, removing neighbours still too close until none are left).</summary>
public static class PeakFinder
{
    public static IReadOnlyList<Peak> FindPeaks(double[] values, double minProminence, int distance)
    {
        var maxima = LocalMaxima(values);
        var withProminence = maxima
            .Select(index => new Peak(index, values[index], Prominence(values, index)))
            .Where(peak => peak.Prominence >= minProminence)
            .ToList();
        return distance <= 1 ? withProminence : ThinByDistance(withProminence, distance);
    }

    /// <summary>Indices of local maxima, taking the midpoint of a flat plateau as SciPy does.</summary>
    private static List<int> LocalMaxima(double[] values)
    {
        var peaks = new List<int>();
        var i = 1;
        while (i < values.Length - 1)
        {
            if (values[i - 1] < values[i])
            {
                var end = i;
                while (end < values.Length - 1 && values[end] == values[end + 1])
                {
                    end++;
                }
                if (end < values.Length - 1 && values[end] > values[end + 1])
                {
                    peaks.Add((i + end) / 2);
                }
                i = end + 1;
            }
            else
            {
                i++;
            }
        }
        return peaks;
    }

    /// <summary>How far `values[peakIndex]` stands out: its value minus the higher of the lowest
    /// point reached walking left and the lowest point reached walking right before the signal
    /// climbs back above the peak's own value (or a signal boundary is reached).</summary>
    private static double Prominence(double[] values, int peakIndex)
    {
        var peakValue = values[peakIndex];
        var leftMin = peakValue;
        for (var i = peakIndex - 1; i >= 0; i--)
        {
            if (values[i] > peakValue)
            {
                break;
            }
            leftMin = Math.Min(leftMin, values[i]);
        }
        var rightMin = peakValue;
        for (var i = peakIndex + 1; i < values.Length; i++)
        {
            if (values[i] > peakValue)
            {
                break;
            }
            rightMin = Math.Min(rightMin, values[i]);
        }
        return peakValue - Math.Max(leftMin, rightMin);
    }

    private static List<Peak> ThinByDistance(List<Peak> peaks, int distance)
    {
        // SciPy's own tie-break for equal heights keeps the later (higher-index) peak: processing
        // the higher index first marks it kept before its earlier, equal-height neighbour can be.
        var ordered = peaks.OrderByDescending(p => p.Value).ThenByDescending(p => p.Index).ToList();
        var removed = new HashSet<int>();
        foreach (var peak in ordered)
        {
            if (removed.Contains(peak.Index))
            {
                continue;
            }
            foreach (var other in peaks)
            {
                if (other.Index != peak.Index && !removed.Contains(other.Index) && Math.Abs(other.Index - peak.Index) < distance)
                {
                    removed.Add(other.Index);
                }
            }
        }
        return peaks.Where(p => !removed.Contains(p.Index)).OrderBy(p => p.Index).ToList();
    }
}
