using System.Security.Cryptography;
using System.Text;

namespace CoachVision.Analysis;

/// <summary>Builds the `modelVersion` every result carries (contract: `ModelVersion`, at most 64
/// characters). It must change whenever the worker's models change (its `poseModel` or
/// `barDetector` string) or this project's rules change (<see cref="RulesVersion"/>, bumped by
/// hand whenever rep/velocity/depth logic here changes in a way that could move a result).
///
/// The worker's `version` field is not required to change on every model swap, so the pose model
/// and bar detector identifiers are folded into a short hash rather than trusted on their own:
/// two worker builds with the same `version` but a different model are still told apart.</summary>
public static class ModelVersioning
{
    /// <summary>Bump this when rep segmentation, velocity scaling, depth judging or confidence
    /// rules change here, even if the worker and its models are untouched.</summary>
    public const string RulesVersion = "r1";

    public const int MaxLength = 64;

    public static string Compute(RawWorker worker)
    {
        var fingerprint = $"{worker.PoseModel}|{worker.BarDetector}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))[..8].ToLowerInvariant();
        // The suffix is what tells two otherwise-identical worker versions apart (the model hash)
        // and records the rules version; truncating the whole composed string from wherever it
        // happens to land can cut that suffix off instead, silently losing the one thing a long
        // `worker.Version` must not be allowed to hide. Bounding `worker.Version` itself, and
        // always keeping the suffix whole, keeps both pieces of information no matter how long
        // the worker's own version string is.
        var suffix = $"-{hash}+{RulesVersion}";
        var maxVersionLength = Math.Max(0, MaxLength - suffix.Length);
        var versionPart = worker.Version.Length <= maxVersionLength ? worker.Version : worker.Version[..maxVersionLength];
        return versionPart + suffix;
    }
}
