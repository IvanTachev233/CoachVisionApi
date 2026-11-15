namespace CoachVision.Analysis.Internal;

/// <summary>The 2D knee angle a squat depth check needs (ADR 0017: 2D landmarks only, never `z`).</summary>
public static class KneeGeometry
{
    /// <summary>The angle at the knee between the thigh (knee to hip) and the shin (knee to ankle),
    /// in degrees; 180 is a straight leg, smaller is more bent.</summary>
    public static double AngleDegrees((double X, double Y) hip, (double X, double Y) knee, (double X, double Y) ankle)
    {
        var toHip = (hip.X - knee.X, hip.Y - knee.Y);
        var toAnkle = (ankle.X - knee.X, ankle.Y - knee.Y);
        var dot = (toHip.Item1 * toAnkle.Item1) + (toHip.Item2 * toAnkle.Item2);
        var magnitude = Length(toHip) * Length(toAnkle);
        if (magnitude <= 0)
        {
            return 180.0;
        }
        var cosine = Math.Clamp(dot / magnitude, -1.0, 1.0);
        return Math.Acos(cosine) * 180.0 / Math.PI;
    }

    private static double Length((double X, double Y) v) => Math.Sqrt((v.X * v.X) + (v.Y * v.Y));
}
