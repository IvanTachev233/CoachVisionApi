using CoachVision.Analysis.Internal;

namespace CoachVision.Analysis.Tests;

public sealed class KneeGeometryTests
{
    [Fact]
    public void A_straight_leg_is_180_degrees()
    {
        var angle = KneeGeometry.AngleDegrees(hip: (0, 0), knee: (0, 10), ankle: (0, 20));

        Assert.Equal(180, angle, precision: 6);
    }

    [Fact]
    public void A_right_angle_bend_is_90_degrees()
    {
        var angle = KneeGeometry.AngleDegrees(hip: (0, 0), knee: (0, 10), ankle: (10, 10));

        Assert.Equal(90, angle, precision: 6);
    }

    [Fact]
    public void A_fully_folded_leg_is_near_zero_degrees()
    {
        var angle = KneeGeometry.AngleDegrees(hip: (0, 0), knee: (0, 10), ankle: (0, 1));

        Assert.True(angle < 10);
    }
}
