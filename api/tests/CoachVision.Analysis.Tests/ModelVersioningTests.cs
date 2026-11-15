using CoachVision.Analysis;

namespace CoachVision.Analysis.Tests;

/// <summary>Direct tests of the composed `modelVersion` string: it must stay within
/// <see cref="ModelVersioning.MaxLength"/> while never losing the model-fingerprint hash or the
/// rules version, whatever the worker's own `version` string looks like.</summary>
public sealed class ModelVersioningTests
{
    private static RawWorker Worker(string version, string poseModel = "m@1", string barDetector = "b@1") =>
        new() { Version = version, PoseModel = poseModel, BarDetector = barDetector };

    [Fact]
    public void A_worker_version_right_at_the_64_character_limit_keeps_the_hash_and_rules_suffix()
    {
        var hashAndRules = ModelVersioning.Compute(Worker("x"))[1..]; // "-{hash}+{rules}", same fingerprint below
        var version = new string('a', ModelVersioning.MaxLength); // exactly 64 chars on its own

        var result = ModelVersioning.Compute(Worker(version));

        Assert.True(result.Length <= ModelVersioning.MaxLength, $"'{result}' ({result.Length} chars) exceeds the {ModelVersioning.MaxLength}-character limit");
        Assert.EndsWith(hashAndRules, result);
    }

    [Fact]
    public void A_worker_version_well_past_the_64_character_limit_still_keeps_the_hash_and_rules_suffix()
    {
        var hashAndRules = ModelVersioning.Compute(Worker("x"))[1..];
        var version = new string('a', 200); // 200 chars: far longer than the whole 64-char budget

        var result = ModelVersioning.Compute(Worker(version));

        Assert.True(result.Length <= ModelVersioning.MaxLength, $"'{result}' ({result.Length} chars) exceeds the {ModelVersioning.MaxLength}-character limit");
        Assert.EndsWith(hashAndRules, result);
    }

    [Fact]
    public void Two_documents_differing_only_in_pose_model_still_get_different_versions_even_with_a_200_char_worker_version()
    {
        var version = new string('b', 200);

        var withModelA = ModelVersioning.Compute(Worker(version, poseModel: "mediapipe-pose-landmarker-full@1.2.2"));
        var withModelB = ModelVersioning.Compute(Worker(version, poseModel: "mediapipe-pose-landmarker-full@1.3.0"));

        // The whole composed string stays within the limit either way...
        Assert.True(withModelA.Length <= ModelVersioning.MaxLength);
        Assert.True(withModelB.Length <= ModelVersioning.MaxLength);
        // ...but a truncation that cuts the hash off (instead of bounding the version part) would
        // make both land on the same truncated prefix, hiding the model change entirely.
        Assert.NotEqual(withModelA, withModelB);
    }

    [Fact]
    public void A_short_worker_version_is_not_truncated_at_all()
    {
        var result = ModelVersioning.Compute(Worker("1.0.0"));

        Assert.StartsWith("1.0.0-", result);
        Assert.EndsWith($"+{ModelVersioning.RulesVersion}", result);
    }
}
