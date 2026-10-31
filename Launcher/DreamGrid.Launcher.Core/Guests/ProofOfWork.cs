using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DreamGrid.Launcher.Core.Guests;

/// <summary>
/// A proof-of-work challenge in ALTCHA's wire format. The browser finds the secret number from 0 to <see cref="MaxNumber"/> whose
/// SHA-256 over <see cref="Salt"/> followed by the number (in decimal) is <see cref="ChallengeHex"/>.
/// </summary>
/// <param name="Algorithm">Always "SHA-256".</param>
/// <param name="ChallengeHex">Lower-case hex of SHA-256(salt + number).</param>
/// <param name="MaxNumber">The largest number to try (the difficulty).</param>
/// <param name="Salt">12 random bytes as hex, then <c>?expires=</c> and the Unix seconds when the challenge stops being accepted.</param>
/// <param name="Signature">Lower-case hex of HMAC-SHA-256(key, <see cref="ChallengeHex"/>): proves this server made the challenge.</param>
public sealed record Challenge(string Algorithm, string ChallengeHex, int MaxNumber, string Salt, string Signature);

/// <summary>
/// Makes and checks proof-of-work challenges for the bot protection (spec 7.6). Stateless: a challenge is signed with a key only
/// this server has, so nothing is remembered between making one and checking its answer. (Accepting an answer once is
/// <c>BotGuard</c>'s job.) The wire format is ALTCHA's, so its widget could replace our own script later.
/// </summary>
public sealed class ProofOfWork(byte[] key, TimeProvider time)
{
    public const string Algorithm = "SHA-256";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private const string ExpiresMark = "?expires=";
    private const int MaxPayloadChars = 2048;

    /// <summary>
    /// The 32-byte key kept in <paramref name="file"/> (as hex, owner-only where the system allows). Made on first use; a file that is
    /// missing or damaged is replaced, which only means challenges made before are no longer accepted.
    /// </summary>
    public static byte[] LoadOrCreateKey(string file)
    {
        try
        {
            if (File.Exists(file) && Convert.FromHexString(File.ReadAllText(file).Trim()) is { Length: 32 } existing)
                return existing;
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException) { }
        var key = RandomNumberGenerator.GetBytes(32);
        AtomicFile.WriteAllText(file, Convert.ToHexStringLower(key));
        return key;
    }

    /// <summary>A new challenge with a random secret number from 0 to <paramref name="maxNumber"/>, good for ten minutes.</summary>
    public Challenge Create(int maxNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxNumber);
        ArgumentOutOfRangeException.ThrowIfEqual(maxNumber, int.MaxValue);
        var salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12))
            + ExpiresMark + (time.GetUtcNow() + Lifetime).ToUnixTimeSeconds();
        return Build(salt, RandomNumberGenerator.GetInt32(0, maxNumber + 1), maxNumber);
    }

    /// <summary>The challenge for a given salt and secret number (what <see cref="Create"/> does once it has picked them).</summary>
    public Challenge Build(string salt, int number, int maxNumber)
    {
        var challenge = ChallengeOf(salt, number);
        return new Challenge(Algorithm, challenge, maxNumber, salt, SignatureOf(challenge));
    }

    /// <summary>
    /// True when <paramref name="payloadBase64"/> (base64 of the JSON <c>{algorithm, challenge, number, salt, signature}</c>) carries
    /// a signature this server made, a salt that has not expired, and a number that really solves the challenge. Anything else,
    /// however malformed, is false.
    /// </summary>
    public bool Verify(string? payloadBase64) => TryVerify(payloadBase64, out _, out _);

    /// <summary>
    /// As <see cref="Verify"/>; <paramref name="expires"/> is when the accepted payload stops being valid and <paramref name="challenge"/>
    /// the challenge it solved (lower-case hex, bound by the signature). The challenge, not the payload text, is what identifies an
    /// answer: the same answer can be written many ways (spaces in the base64, other JSON spacing or key order).
    /// </summary>
    public bool TryVerify(string? payloadBase64, out DateTimeOffset expires, out string challenge)
    {
        expires = default;
        challenge = "";
        if (string.IsNullOrEmpty(payloadBase64) || payloadBase64.Length > MaxPayloadChars) return false;
        try
        {
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payloadBase64));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (Text(root, "algorithm") != Algorithm) return false;
            if (Text(root, "challenge") is not { } claimed || Text(root, "salt") is not { } salt || Text(root, "signature") is not { } signature)
                return false;
            if (!root.TryGetProperty("number", out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out var number) || number < 0)
                return false;

            if (!Same(signature, SignatureOf(claimed))) return false;
            if (!Same(claimed, ChallengeOf(salt, number))) return false;

            var at = salt.LastIndexOf(ExpiresMark, StringComparison.Ordinal);
            if (at < 0 || !long.TryParse(salt.AsSpan(at + ExpiresMark.Length), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                return false;
            expires = DateTimeOffset.FromUnixTimeSeconds(seconds);
            // The signature covers the challenge, not the salt's expiry, and "salt + number" can be cut in another place (a digit of the
            // number moved onto the end of the expiry). That can only make the expiry far larger, so one beyond the lifetime is refused.
            var now = time.GetUtcNow();
            if (expires <= now || expires > now + Lifetime) return false;
            challenge = claimed;
            return true;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private string ChallengeOf(string salt, int number) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(salt + number.ToString(System.Globalization.CultureInfo.InvariantCulture))));

    private string SignatureOf(string challenge) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(challenge)));

    private static string? Text(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Same(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
