namespace CoachVision.Analysis.Internal;

/// <summary>A track's height per frame (pixels, larger is lower; `null` where nothing usable was
/// found) and the unit (pixels) a rep must move by to count, e.g. the plate's own radius for the
/// bar, or a shoulder-to-hip length for the shoulders.</summary>
public readonly record struct Track(double[] Times, double?[] Heights, double? Unit);

/// <summary>A counted rep: indices into the track's own frame array for its start (the top before
/// descending), bottom (the lowest point) and end (lockout, the top after ascending).</summary>
public readonly record struct RepWindow(int StartIndex, int BottomIndex, int EndIndex);

public static class Tracks
{
    /// <summary>Frames with too little data to trust a track's unit or shape at all: fewer than
    /// this many frames found makes the gap-filled track meaningless.</summary>
    public const int MinKnownFrames = 10;

    public static Track BarTrack(IReadOnlyList<RawFrame> frames)
    {
        var times = new double[frames.Count];
        var heights = new double?[frames.Count];
        var radii = new List<double>();
        for (var i = 0; i < frames.Count; i++)
        {
            times[i] = frames[i].T;
            var bar = frames[i].Bar;
            heights[i] = bar?.Y;
            if (bar is not null)
            {
                radii.Add(bar.RadiusPx);
            }
        }
        double? unit = radii.Count > 0 ? Median(radii) : null;
        return new Track(times, heights, unit);
    }

    public static Track ShoulderTrack(IReadOnlyList<RawFrame> frames)
    {
        var times = new double[frames.Count];
        var heights = new double?[frames.Count];
        var torsos = new List<double>();
        for (var i = 0; i < frames.Count; i++)
        {
            times[i] = frames[i].T;
            var pose = frames[i].Pose;
            if (pose is not null && pose.TryGetValue(LandmarkNames.LeftShoulder, out var ls) && pose.TryGetValue(LandmarkNames.RightShoulder, out var rs))
            {
                heights[i] = (ls.Y + rs.Y) / 2;
                if (pose.TryGetValue(LandmarkNames.LeftHip, out var lh) && pose.TryGetValue(LandmarkNames.RightHip, out var rh))
                {
                    var shoulderMidX = (ls.X + rs.X) / 2;
                    var shoulderMidY = (ls.Y + rs.Y) / 2;
                    var hipMidX = (lh.X + rh.X) / 2;
                    var hipMidY = (lh.Y + rh.Y) / 2;
                    torsos.Add(Math.Sqrt(Math.Pow(shoulderMidX - hipMidX, 2) + Math.Pow(shoulderMidY - hipMidY, 2)));
                }
            }
        }
        double? unit = torsos.Count > 0 ? Percentile(torsos, 0.9) : null;
        return new Track(times, heights, unit);
    }

    /// <summary>Reps in a track, or null when there isn't enough data to count at all (too few
    /// known frames, no unit, or the known frames don't span forward in time). `depthUnits` is how
    /// far, in track units, the signal must fall and rise back for a bottom to count as a rep
    /// (the engine uses 1.0 plate radius for the bar, 0.5 torso lengths for the shoulders).</summary>
    public static IReadOnlyList<RepWindow>? CountReps(Track track, double depthUnits, double minRepGapSeconds)
    {
        var known = KnownIndices(track.Heights);
        if (known.Length < MinKnownFrames || track.Unit is null or 0)
        {
            return null;
        }
        var first = known[0];
        var last = known[^1];
        if (track.Times[last] <= track.Times[first])
        {
            return null;
        }
        var denseTimes = new double[last - first + 1];
        for (var i = 0; i < denseTimes.Length; i++)
        {
            denseTimes[i] = track.Times[first + i];
        }
        var knownRelative = known.Select(i => i - first).ToArray();
        // FillGaps indexes `values` by the same positions as `times`, so the array passed in
        // must span the whole dense window, with only the known positions actually read.
        var denseValues = new double[last - first + 1];
        foreach (var i in knownRelative)
        {
            denseValues[i] = track.Heights[first + i]!.Value;
        }
        var filled = Smoothing.FillGaps(denseTimes, denseValues, knownRelative);
        for (var i = 0; i < filled.Length; i++)
        {
            filled[i] /= track.Unit.Value;
        }
        var median = Smoothing.MedianFilter(filled, 5);
        var smoothed = Smoothing.MovingAverage(median, 5);
        var rate = (smoothed.Length - 1) / (denseTimes[^1] - denseTimes[0]);
        var minDistance = Math.Max(1, (int)Math.Round(minRepGapSeconds * rate));
        var peaks = PeakFinder.FindPeaks(smoothed, depthUnits, minDistance);
        return BuildWindows(peaks, smoothed, first);
    }

    /// <summary>Each peak (a rep's bottom) paired with the nearest valley before it (the rep's
    /// start, the top before descending) and after it (the rep's end, lockout). Indices are
    /// translated back to the original track's own frame indices by adding `offset`.</summary>
    private static List<RepWindow> BuildWindows(IReadOnlyList<Peak> peaks, double[] smoothed, int offset)
    {
        var windows = new List<RepWindow>();
        for (var i = 0; i < peaks.Count; i++)
        {
            var bottom = peaks[i].Index;
            var searchStart = i == 0 ? 0 : peaks[i - 1].Index;
            var searchEnd = i == peaks.Count - 1 ? smoothed.Length - 1 : peaks[i + 1].Index;
            var start = ArgMin(smoothed, searchStart, bottom);
            var end = ArgMin(smoothed, bottom, searchEnd);
            windows.Add(new RepWindow(start + offset, bottom + offset, end + offset));
        }
        return windows;
    }

    private static int ArgMin(double[] values, int from, int to)
    {
        var best = from;
        for (var i = from; i <= to; i++)
        {
            if (values[i] < values[best])
            {
                best = i;
            }
        }
        return best;
    }

    public static int[] KnownIndices(double?[] heights)
    {
        var indices = new List<int>();
        for (var i = 0; i < heights.Length; i++)
        {
            if (heights[i] is not null)
            {
                indices.Add(i);
            }
        }
        return indices.ToArray();
    }

    public static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
    }

    /// <summary>Linear-interpolated percentile, matching `numpy.percentile`'s default method.</summary>
    public static double Percentile(IReadOnlyList<double> values, double fraction)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 1)
        {
            return sorted[0];
        }
        var position = fraction * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sorted[lower];
        }
        var weight = position - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * weight;
    }
}
