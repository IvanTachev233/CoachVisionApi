using CoachVision.Data.Security;

namespace CoachVision.Data.Tests;

/// <summary>A clip's measured length against its tenant's limit — pure, no database needed.</summary>
public sealed class ClipLengthTests
{
    [Fact]
    public void Exactly_the_limit_is_accepted()
    {
        var result = ClipLength.Check(TimeSpan.FromSeconds(60), maxClipLengthSeconds: 60);

        Assert.Equal(ClipLengthOutcome.Accepted, result.Outcome);
        Assert.Null(result.Failure);
    }

    [Fact]
    public void One_second_over_the_limit_is_refused_with_the_contracts_failure_shape()
    {
        var result = ClipLength.Check(TimeSpan.FromSeconds(61), maxClipLengthSeconds: 60);

        Assert.Equal(ClipLengthOutcome.TooLong, result.Outcome);
        Assert.NotNull(result.Failure);
        Assert.Equal("video_too_long", result.Failure!.Reason);
        Assert.False(result.Failure.Retryable);
        Assert.Equal("The limit is 60 seconds.", result.Failure.Detail);
    }
}
