using System.Buffers.Binary;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DreamGrid.Launcher.Core.Guests;

/// <summary>A picture of letters, the question that is its text alternative, and the signed token that holds the answers' hashes.</summary>
/// <param name="Svg">The picture: five stroked glyphs and some noise lines, with no <c>&lt;text&gt;</c> element.</param>
/// <param name="Question">The text alternative, for example "What is four plus seven?".</param>
/// <param name="Token">What the form posts back with the answers (base64url).</param>
public sealed record CaptchaChallenge(string Svg, string Question, string Token);

/// <summary>What <see cref="ImageCaptcha.Check"/> found.</summary>
public enum CaptchaResult
{
    /// <summary>The token is missing, damaged, forged or out of date: there is no challenge to answer.</summary>
    Invalid,

    /// <summary>The token is good and neither answer is right.</summary>
    Wrong,

    /// <summary>The token is good and at least one answer is right.</summary>
    Right,
}

/// <summary>
/// The image CAPTCHA (spec 7.6, layer C): a self-hosted picture of five letters, with a sum written in words as the alternative for
/// people who can't see it; either answer passes. Stateless, like <see cref="ProofOfWork"/>: the token carries the hashes of both
/// answers and its own expiry, signed with a key only this server has, so nothing is remembered between making a challenge and
/// checking its answer. (Accepting a token once is <c>BotGuard</c>'s job.) The answers are hashed with the key, so the token does not
/// give them away and they can't be guessed offline. The key may be the proof-of-work key: every signature here is prefixed with a
/// label of its own, so one can never stand in for the other.
/// </summary>
public sealed class ImageCaptcha(byte[] key, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int Length = 5;

    private const int NonceBytes = 16, HashBytes = 32;
    private const int BodyBytes = NonceBytes + 8 + HashBytes + HashBytes;
    private const int MaxTokenChars = 256;
    private const int MaxAnswerChars = 64;

    private static readonly string[] Words =
        ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
         "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen"];

    /// <summary>A new challenge: five random characters, two random numbers from 1 to 9, good for ten minutes.</summary>
    public CaptchaChallenge Create()
    {
        var letters = new StringBuilder(Length);
        for (var i = 0; i < Length; i++)
            letters.Append(CaptchaGlyphs.Alphabet[RandomNumberGenerator.GetInt32(CaptchaGlyphs.Alphabet.Length)]);
        return Build(letters.ToString(), RandomNumberGenerator.GetInt32(1, 10), RandomNumberGenerator.GetInt32(1, 10));
    }

    /// <summary>The challenge for given letters and numbers (what <see cref="Create"/> does once it has picked them).</summary>
    internal CaptchaChallenge Build(string letters, int first, int second)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(letters.Length, Length);
        if (!letters.All(CaptchaGlyphs.Has)) throw new ArgumentException("Letters outside the CAPTCHA alphabet.", nameof(letters));
        ArgumentOutOfRangeException.ThrowIfLessThan(first, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(first, 9);
        ArgumentOutOfRangeException.ThrowIfLessThan(second, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(second, 9);

        var body = new byte[BodyBytes];
        RandomNumberGenerator.Fill(body.AsSpan(0, NonceBytes));
        BinaryPrimitives.WriteInt64BigEndian(body.AsSpan(NonceBytes, 8), (time.GetUtcNow() + Lifetime).ToUnixTimeSeconds());
        LettersHash(body.AsSpan(0, NonceBytes), letters).CopyTo(body.AsSpan(NonceBytes + 8));
        SumHash(body.AsSpan(0, NonceBytes), first + second).CopyTo(body.AsSpan(NonceBytes + 8 + HashBytes));

        var token = new byte[BodyBytes + HashBytes];
        body.CopyTo(token, 0);
        Sign(body).CopyTo(token.AsSpan(BodyBytes));
        return new CaptchaChallenge(Draw(letters), $"What is {Words[first]} plus {Words[second]}?", Base64Url.EncodeToString(token));
    }

    /// <summary>True when the token is good and either answer is right (letters in any case, the sum as digits or as a word).</summary>
    public bool Verify(string? token, string? imageAnswer, string? questionAnswer) =>
        Check(token, imageAnswer, questionAnswer, out _, out _) == CaptchaResult.Right;

    /// <summary>
    /// Checks the token and the answers. <paramref name="expires"/> is when the token stops being valid and <paramref name="id"/> names
    /// the challenge (the token's random part, which the signature binds): that, not the text that was posted, is what identifies a
    /// token that must be accepted once. Both are set whenever the result is not <see cref="CaptchaResult.Invalid"/>. Anything
    /// malformed is <see cref="CaptchaResult.Invalid"/>, never an exception.
    /// </summary>
    public CaptchaResult Check(string? token, string? imageAnswer, string? questionAnswer, out DateTimeOffset expires, out string id)
    {
        expires = default;
        id = "";
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenChars) return CaptchaResult.Invalid;
        byte[] bytes;
        try
        {
            bytes = Base64Url.DecodeFromChars(token);
        }
        catch (FormatException)
        {
            return CaptchaResult.Invalid;
        }
        if (bytes.Length != BodyBytes + HashBytes) return CaptchaResult.Invalid;

        var body = bytes.AsSpan(0, BodyBytes);
        if (!CryptographicOperations.FixedTimeEquals(Sign(body), bytes.AsSpan(BodyBytes))) return CaptchaResult.Invalid;

        var seconds = BinaryPrimitives.ReadInt64BigEndian(body.Slice(NonceBytes, 8));
        if (seconds is < 0 or > 253_402_300_799) return CaptchaResult.Invalid; // the range DateTimeOffset can hold
        var until = DateTimeOffset.FromUnixTimeSeconds(seconds);
        var now = time.GetUtcNow();
        if (until <= now || until > now + Lifetime) return CaptchaResult.Invalid;

        expires = until;
        var nonce = body[..NonceBytes];
        id = Convert.ToHexStringLower(nonce);

        var right = false;
        if (Letters(imageAnswer) is { } letters)
            right |= CryptographicOperations.FixedTimeEquals(LettersHash(nonce, letters), body.Slice(NonceBytes + 8, HashBytes));
        if (Sum(questionAnswer) is { } sum)
            right |= CryptographicOperations.FixedTimeEquals(SumHash(nonce, sum), body.Slice(NonceBytes + 8 + HashBytes, HashBytes));
        return right ? CaptchaResult.Right : CaptchaResult.Wrong;
    }

    /// <summary>What was typed for the picture: letters in any case, with spaces and dashes dropped; null when it can't be an answer.</summary>
    private static string? Letters(string? typed)
    {
        if (string.IsNullOrEmpty(typed) || typed.Length > MaxAnswerChars) return null;
        var letters = new string(typed.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();
        return letters.Length == Length ? letters : null;
    }

    /// <summary>What was typed for the question: digits or a number word, trimmed, in any case; null when it is neither.</summary>
    private static int? Sum(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed) || typed.Length > MaxAnswerChars) return null;
        var text = typed.Trim().ToLowerInvariant();
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return number;
        var at = Array.IndexOf(Words, text);
        return at >= 0 ? at : null;
    }

    private byte[] LettersHash(ReadOnlySpan<byte> nonce, string letters) => Hash("letters:", nonce, letters);

    private byte[] SumHash(ReadOnlySpan<byte> nonce, int sum) => Hash("sum:", nonce, sum.ToString(CultureInfo.InvariantCulture));

    private byte[] Hash(string label, ReadOnlySpan<byte> nonce, string answer) =>
        HMACSHA256.HashData(key, Join("dreamgrid-captcha-answer:"u8, nonce, Encoding.ASCII.GetBytes(label + answer)));

    private byte[] Sign(ReadOnlySpan<byte> body) =>
        HMACSHA256.HashData(key, Join("dreamgrid-captcha-token:"u8, body, []));

    private static byte[] Join(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c)
    {
        var joined = new byte[a.Length + b.Length + c.Length];
        a.CopyTo(joined);
        b.CopyTo(joined.AsSpan(a.Length));
        c.CopyTo(joined.AsSpan(a.Length + b.Length));
        return joined;
    }

    /// <summary>The picture: each glyph rotated -20 to 20 degrees, offset up or down, with jitter on its points, then six noise lines over it.</summary>
    private static string Draw(string letters)
    {
        const int width = 200, height = 60, cell = 36, scale = 6;
        var svg = new StringBuilder()
            .Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width} {height}\" width=\"{width}\" height=\"{height}\">")
            .Append($"<rect width=\"{width}\" height=\"{height}\" fill=\"#ffffff\"/>");
        var left = (width - cell * Length) / 2 + (cell - CaptchaGlyphs.Width * scale) / 2;
        for (var i = 0; i < letters.Length; i++)
        {
            var x = left + i * cell + Between(-3, 3);
            var y = (height - CaptchaGlyphs.Height * scale) / 2 + Between(-5, 5);
            var angle = Between(-20, 20);
            svg.Append(CultureInfo.InvariantCulture,
                $"<g transform=\"translate({x:0.#} {y:0.#}) rotate({angle:0.#} {CaptchaGlyphs.Width * scale / 2:0.#} {CaptchaGlyphs.Height * scale / 2:0.#})\">")
               .Append("<path fill=\"none\" stroke=\"#202020\" stroke-width=\"3\" stroke-linecap=\"round\" stroke-linejoin=\"round\" d=\"")
               .Append(CaptchaGlyphs.Path(letters[i], scale, 0.12, () => Between(-1, 1)))
               .Append("\"/></g>");
        }
        for (var i = 0; i < 6; i++)
        {
            svg.Append(CultureInfo.InvariantCulture,
                $"<line x1=\"{Between(0, width):0.#}\" y1=\"{Between(0, height):0.#}\" x2=\"{Between(0, width):0.#}\" y2=\"{Between(0, height):0.#}\" stroke=\"#707070\" stroke-width=\"1.5\" stroke-linecap=\"round\"/>");
        }
        return svg.Append("</svg>").ToString();
    }

    /// <summary>A random number from <paramref name="min"/> to <paramref name="max"/>, from the system's secure source.</summary>
    private static double Between(double min, double max) =>
        min + RandomNumberGenerator.GetInt32(0, 1_000_001) / 1_000_000.0 * (max - min);
}
