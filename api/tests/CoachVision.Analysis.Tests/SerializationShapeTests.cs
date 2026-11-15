using System.Text.Json;
using System.Text.Json.Nodes;
using CoachVision.Analysis;

namespace CoachVision.Analysis.Tests;

/// <summary>Every result must serialise to exactly the contract's shapes (camelCase fields, the
/// `outcome` discriminator, no NaN or Infinity ever).</summary>
public sealed class SerializationShapeTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void Analysed_results_serialise_with_the_contracts_members_and_discriminator()
    {
        var raw = SyntheticClipBuilder.Build(frameCount: 300, shoulderYOf: SyntheticClipBuilder.Reps(2, 2.5, 250));
        var results = AnalysisEngine.Analyze(raw, SyntheticClipBuilder.SquatRequest(SyntheticClipBuilder.AllFeatures));

        var json = JsonSerializer.Serialize(results, Options);
        var node = JsonNode.Parse(json)!.AsObject();

        Assert.Equal("1.0", node["schemaVersion"]!.GetValue<string>());
        var repCount = node["rep_count"]!.AsObject();
        Assert.Equal("analysed", repCount["outcome"]!.GetValue<string>());
        Assert.True(repCount.ContainsKey("confidence"));
        Assert.True(repCount.ContainsKey("modelVersion"));
        Assert.True(repCount.ContainsKey("count"));
        var firstRep = repCount["reps"]![0]!.AsObject();
        Assert.True(firstRep.ContainsKey("startS"));
        Assert.True(firstRep.ContainsKey("bottomS"));
        Assert.True(firstRep.ContainsKey("endS"));

        var velocity = node["velocity"]!.AsObject();
        Assert.Equal("analysed", velocity["outcome"]!.GetValue<string>());
        var firstVelocityRep = velocity["reps"]![0]!.AsObject();
        Assert.True(firstVelocityRep.ContainsKey("meanConcentricMps"));
        Assert.True(firstVelocityRep.ContainsKey("peakConcentricMps"));

        var barPath = node["bar_path"]!.AsObject();
        Assert.Equal("analysed", barPath["outcome"]!.GetValue<string>());
        var firstSample = barPath["samples"]![0]!.AsObject();
        foreach (var key in new[] { "t", "x", "y", "px", "py" })
        {
            Assert.True(firstSample.ContainsKey(key));
        }

        var squatDepth = node["verification_checks"]!["squat_depth"]!.AsObject();
        Assert.Equal("analysed", squatDepth["outcome"]!.GetValue<string>());
        Assert.True(squatDepth.ContainsKey("allRepsPassed"));
        var firstDepthRep = squatDepth["reps"]![0]!.AsObject();
        Assert.True(firstDepthRep.ContainsKey("passed"));
        Assert.True(firstDepthRep.ContainsKey("minKneeAngleDeg"));
    }

    [Fact]
    public void An_unanalysable_result_serialises_with_outcome_reason_and_model_version_only()
    {
        var raw = SyntheticClipBuilder.Build(frameCount: 150, shoulderYOf: SyntheticClipBuilder.Reps(3, 2.5, 250), includeBar: false);
        var results = AnalysisEngine.Analyze(raw, SyntheticClipBuilder.SquatRequest(new[] { Feature.Velocity }));

        var json = JsonSerializer.Serialize(results, Options);
        var node = JsonNode.Parse(json)!.AsObject();
        var velocity = node["velocity"]!.AsObject();

        Assert.Equal("unanalysable", velocity["outcome"]!.GetValue<string>());
        Assert.Equal("plates_not_found", velocity["reason"]!.GetValue<string>());
        Assert.True(velocity.ContainsKey("modelVersion"));
        Assert.False(velocity.ContainsKey("confidence"));
        Assert.False(velocity.ContainsKey("reps"));
    }

    [Fact]
    public void No_result_ever_serialises_NaN_or_infinity()
    {
        var raw = SyntheticClipBuilder.Build(frameCount: 300, shoulderYOf: SyntheticClipBuilder.Reps(2, 2.5, 250));
        var results = AnalysisEngine.Analyze(raw, SyntheticClipBuilder.SquatRequest(SyntheticClipBuilder.AllFeatures));

        var json = JsonSerializer.Serialize(results, Options);

        Assert.DoesNotContain("NaN", json);
        Assert.DoesNotContain("Infinity", json);
    }

    [Fact]
    public void An_empty_frames_list_is_handled_without_throwing()
    {
        var raw = new RawOutputDocument
        {
            SchemaVersion = "1.0",
            JobId = "job_01JC8YX4D2F7H1K9M3P5Q8S0TV",
            Worker = new RawWorker { Version = "1.0.0", PoseModel = "m@1", BarDetector = "b@1" },
            Video = new RawVideo { Width = 1080, Height = 1920, FrameRate = 30, DurationS = 0, FrameCount = 0 },
            Frames = Array.Empty<RawFrame>(),
        };

        var results = AnalysisEngine.Analyze(raw, SyntheticClipBuilder.SquatRequest(SyntheticClipBuilder.AllFeatures));

        Assert.IsAssignableFrom<IUnanalysable>(results.RepCount);
    }

    [Fact]
    public void A_single_frame_is_handled_without_throwing()
    {
        var raw = SyntheticClipBuilder.Build(frameCount: 1, shoulderYOf: _ => SyntheticClipBuilder.StandingShoulderY);

        var results = AnalysisEngine.Analyze(raw, SyntheticClipBuilder.SquatRequest(SyntheticClipBuilder.AllFeatures));

        Assert.IsAssignableFrom<IUnanalysable>(results.RepCount);
    }

    [Fact]
    public void All_poses_null_is_handled_without_throwing()
    {
        var raw = SyntheticClipBuilder.Build(frameCount: 150, shoulderYOf: SyntheticClipBuilder.Reps(2, 2.5, 250), includePose: false);

        var results = AnalysisEngine.Analyze(raw, SyntheticClipBuilder.SquatRequest(SyntheticClipBuilder.AllFeatures));

        Assert.Equal(ReasonCodes.PoseNotFound, Assert.IsAssignableFrom<IUnanalysable>(results.RepCount).Reason);
    }

    /// <summary>Every object shape the contract defines, by its member names. `outcome` plus these
    /// is exactly what must appear; nothing the contract doesn't name, and never a JSON null
    /// (an optional member like `detail` or a rep's `confidence` must be left out entirely, not
    /// sent as null) — proven here with a bare camelCase policy, nothing else on the options, so
    /// the shape can't be hiding behind a caller's own ignore-nulls setting.</summary>
    private static readonly Dictionary<string, string[]> AllowedMembersByOutcomeKind = new()
    {
        ["unanalysable"] = new[] { "outcome", "reason", "detail", "modelVersion" },
        ["bar_path.analysed"] = new[] { "outcome", "confidence", "modelVersion", "samples" },
        ["bar_path.sample"] = new[] { "t", "x", "y", "px", "py" },
        ["velocity.analysed"] = new[] { "outcome", "confidence", "modelVersion", "reps" },
        ["velocity.rep"] = new[] { "rep", "meanConcentricMps", "peakConcentricMps", "confidence" },
        ["rep_count.analysed"] = new[] { "outcome", "confidence", "modelVersion", "count", "reps" },
        ["rep_count.rep"] = new[] { "rep", "startS", "bottomS", "endS" },
        ["squat_depth.analysed"] = new[] { "outcome", "confidence", "modelVersion", "allRepsPassed", "reps" },
        ["squat_depth.rep"] = new[] { "rep", "passed", "minKneeAngleDeg", "confidence" },
    };

    [Fact]
    public void Every_result_type_serialises_with_no_null_and_no_undeclared_member()
    {
        var happyPath = SyntheticClipBuilder.Build(frameCount: 300, shoulderYOf: SyntheticClipBuilder.Reps(2, 2.5, 250));
        var happyResults = AnalysisEngine.Analyze(happyPath, SyntheticClipBuilder.SquatRequest(SyntheticClipBuilder.AllFeatures));
        var unanalysableSource = SyntheticClipBuilder.Build(frameCount: 150, shoulderYOf: SyntheticClipBuilder.Reps(3, 2.5, 250), includeBar: false);
        var unanalysableResults = AnalysisEngine.Analyze(unanalysableSource, SyntheticClipBuilder.SquatRequest(SyntheticClipBuilder.AllFeatures));

        foreach (var results in new[] { happyResults, unanalysableResults })
        {
            var json = JsonSerializer.Serialize(results, Options);
            var node = JsonNode.Parse(json)!;
            AssertNoNullAndKnownShape(node);
        }
    }

    private static void AssertNoNullAndKnownShape(JsonNode? node)
    {
        switch (node)
        {
            case null:
                Assert.Fail("a JSON null was found where the contract defines no nullable member");
                break;
            case JsonObject obj:
                var keys = obj.Select(kv => kv.Key).ToHashSet();
                var kind = ShapeKindOf(obj);
                if (kind is not null)
                {
                    var allowed = AllowedMembersByOutcomeKind[kind];
                    Assert.True(keys.IsSubsetOf(allowed), $"{kind} has unexpected members: {string.Join(",", keys.Except(allowed))}");
                }
                foreach (var (_, value) in obj)
                {
                    AssertNoNullAndKnownShape(value);
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    AssertNoNullAndKnownShape(item);
                }
                break;
        }
    }

    /// <summary>Which entry of <see cref="AllowedMembersByOutcomeKind"/> an object matches, by its
    /// own members (there is no shared discriminator across feature kinds, so this recognises
    /// each shape by what it alone looks like).</summary>
    private static string? ShapeKindOf(JsonObject obj)
    {
        if (obj.ContainsKey("outcome") && obj["outcome"]!.GetValue<string>() == "unanalysable")
        {
            return "unanalysable";
        }
        if (obj.ContainsKey("outcome") && obj.ContainsKey("samples"))
        {
            return "bar_path.analysed";
        }
        if (obj.ContainsKey("outcome") && obj.ContainsKey("count"))
        {
            return "rep_count.analysed";
        }
        if (obj.ContainsKey("outcome") && obj.ContainsKey("allRepsPassed"))
        {
            return "squat_depth.analysed";
        }
        if (obj.ContainsKey("outcome") && obj.ContainsKey("reps"))
        {
            return "velocity.analysed";
        }
        if (obj.ContainsKey("px") && obj.ContainsKey("py"))
        {
            return "bar_path.sample";
        }
        if (obj.ContainsKey("meanConcentricMps"))
        {
            return "velocity.rep";
        }
        if (obj.ContainsKey("startS"))
        {
            return "rep_count.rep";
        }
        if (obj.ContainsKey("passed"))
        {
            return "squat_depth.rep";
        }
        return null;
    }
}
