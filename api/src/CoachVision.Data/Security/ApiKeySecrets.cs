using System.Security.Cryptography;
using System.Text;

namespace CoachVision.Data.Security;

/// <summary>
/// Generates and hashes tenant API keys. "Shown once": the plaintext this type returns
/// exists only in that return value — nothing here logs it, and the caller must not either.
///
/// Format: <c>vk_live_</c> or <c>vk_test_</c> followed by 256 bits of CSPRNG entropy, URL-safe
/// base64 (no padding, so the key is one path/query-safe token with no characters that need
/// escaping). 256 bits was chosen over, say, 128: brute-forcing the secret by guessing is what the
/// entropy defends against (the hash below defends the stored value, not the key space), and 256
/// bits costs nothing extra to generate, store or compare while closing off any argument about
/// headroom.
///
/// Stored value: SHA-256 of the full key string (prefix included — binding the hash to the
/// prefix means a <c>vk_live_</c> and <c>vk_test_</c> string that happened to share secret bytes
/// would still hash differently, though that collision is already astronomically unlikely on its
/// own). Plain SHA-256, not a slow/salted hash (bcrypt, Argon2, PBKDF2): those exist to slow down
/// guessing a *low-entropy* secret (a human password) from its hash. A 256-bit random key has no
/// guessable structure to begin with — an attacker who stole the hash table would need to invert
/// SHA-256 or search a 256-bit space, not guess a dictionary word, so a fast hash that never
/// becomes a bottleneck at authentication time is the better trade here. Nothing about this
/// decision is a secret: the point of hashing at all is to make the stored column non-reversible,
/// which SHA-256 already guarantees (one-way, no feasible second preimage).
///
/// Lookup is by exact match on the hash column (a unique index), not a linear scan with a
/// constant-time compare: equality on a 256-bit hash gives an attacker no exploitable timing
/// signal (there is no "closer" or "further" hash to time against — Postgres's btree equality
/// check on fixed-size bytes doesn't leak the original key's byte-by-byte difference the way a
/// hand-rolled comparison of secrets in application code can), so no separate constant-time
/// compare was added on top.
/// </summary>
public static class ApiKeySecrets
{
    public const string LiveKind = "live";
    public const string TestKind = "test";

    /// <summary>Generates one key for the given kind. Returns the plaintext (shown once) and its hash (stored).</summary>
    public static (string Plaintext, byte[] Hash) Generate(string kind)
    {
        if (kind != LiveKind && kind != TestKind)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, $"kind must be \"{LiveKind}\" or \"{TestKind}\".");
        }

        var secret = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var plaintext = $"vk_{kind}_{secret}";
        return (plaintext, Hash(plaintext));
    }

    /// <summary>Hashes a presented key the same way, for comparison against the stored column.</summary>
    public static byte[] Hash(string plaintext) => SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));

    /// <summary>The kind encoded in a key's own prefix, or null if the string isn't shaped like one of ours.</summary>
    public static string? KindFromPrefix(string plaintext)
    {
        if (plaintext.StartsWith("vk_live_", StringComparison.Ordinal))
        {
            return LiveKind;
        }

        if (plaintext.StartsWith("vk_test_", StringComparison.Ordinal))
        {
            return TestKind;
        }

        return null;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
