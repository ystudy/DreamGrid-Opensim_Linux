using System.Globalization;
using System.Text.RegularExpressions;
using DreamGrid.Launcher.Core.Guests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The image CAPTCHA: the picture and its text alternative, the signed token, and refusing everything else.</summary>
public sealed class ImageCaptchaTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)(i + 100)).ToArray();

    private static (ImageCaptcha Captcha, FakeTimeProvider Time) Rig()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        return (new ImageCaptcha(Key, time), time);
    }

    [Fact]
    public void Svg_has_no_text_element_and_no_answer()
    {
        var (captcha, _) = Rig();

        for (var i = 0; i < 40; i++)
        {
            var c = captcha.Create();
            Assert.StartsWith("<svg ", c.Svg);
            Assert.DoesNotContain("<text", c.Svg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<script", c.Svg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(@"\son\w+\s*=", c.Svg);
            Assert.Equal(5, Regex.Count(c.Svg, "<path "));
            Assert.Equal(6, Regex.Count(c.Svg, "<line "));
        }

        // The letters and the sum are in none of the three things the visitor's browser receives.
        var known = captcha.Build("ACDEF", 4, 7);
        foreach (var part in new[] { known.Svg, known.Question, known.Token })
        {
            Assert.DoesNotContain("ACDEF", part, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("eleven", part, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal("What is four plus seven?", known.Question);
    }

    [Fact]
    public void Every_character_of_the_alphabet_can_be_drawn_and_looks_different()
    {
        var (captcha, _) = Rig();
        Assert.Equal(23, CaptchaGlyphs.Alphabet.Length);

        var shapes = new HashSet<string>();
        foreach (var ch in CaptchaGlyphs.Alphabet)
        {
            Assert.True(CaptchaGlyphs.Has(ch), ch.ToString());
            // No jitter, so the same character gives the same path.
            shapes.Add(CaptchaGlyphs.Path(ch, 6, 0, () => 0));
        }
        Assert.Equal(CaptchaGlyphs.Alphabet.Length, shapes.Count);
        Assert.False(CaptchaGlyphs.Has('O'));
        Assert.Throws<ArgumentException>(() => captcha.Build("ABCDE", 1, 1));
    }

    [Fact]
    public void Glyphs_are_rotated_within_twenty_degrees_and_stay_in_the_picture()
    {
        var (captcha, _) = Rig();
        var angles = new List<double>();

        for (var i = 0; i < 60; i++)
        {
            var svg = captcha.Create().Svg;
            foreach (Match m in Regex.Matches(svg, @"rotate\((-?[\d.]+) "))
                angles.Add(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            foreach (Match m in Regex.Matches(svg, @"translate\((-?[\d.]+) (-?[\d.]+)\)"))
            {
                Assert.InRange(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), 0, 200 - 24);
                Assert.InRange(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), 0, 60 - 36);
            }
        }

        Assert.Equal(300, angles.Count);
        Assert.All(angles, a => Assert.InRange(a, -20, 20));
        Assert.Contains(angles, a => a < -5);
        Assert.Contains(angles, a => a > 5);
    }

    [Fact]
    public void Right_letters_pass()
    {
        var (captcha, _) = Rig();
        var c = captcha.Build("AC3DE", 2, 3);

        Assert.True(captcha.Verify(c.Token, "AC3DE", null));
        Assert.True(captcha.Verify(c.Token, "ac3de", ""));
        Assert.True(captcha.Verify(c.Token, "  Ac3 dE\n", null));
        Assert.True(captcha.Verify(c.Token, "A-C-3-D-E", "wrong"));
    }

    [Fact]
    public void Right_question_answer_passes_as_digits_or_words()
    {
        var (captcha, _) = Rig();
        var c = captcha.Build("AC3DE", 4, 7);

        foreach (var answer in new[] { "11", " 11 ", "eleven", "Eleven", "  ELEVEN\t" })
            Assert.True(captcha.Verify(c.Token, null, answer), answer);
        // Either one is enough, and a wrong one beside a right one does not spoil it.
        Assert.True(captcha.Verify(c.Token, "ZZZZZ", "11"));

        var big = captcha.Build("AC3DE", 9, 9);
        Assert.True(captcha.Verify(big.Token, null, "eighteen"));
        Assert.True(captcha.Verify(big.Token, null, "18"));
        var small = captcha.Build("AC3DE", 1, 1);
        Assert.True(captcha.Verify(small.Token, null, "two"));
    }

    [Fact]
    public void Wrong_answers_fail()
    {
        var (captcha, _) = Rig();
        var c = captcha.Build("AC3DE", 4, 7);

        Assert.False(captcha.Verify(c.Token, "AC3DF", "12"));
        Assert.False(captcha.Verify(c.Token, null, null));
        Assert.False(captcha.Verify(c.Token, "", ""));
        Assert.False(captcha.Verify(c.Token, "   ", "   "));
        Assert.False(captcha.Verify(c.Token, "AC3D", null)); // too short
        Assert.False(captcha.Verify(c.Token, "AC3DEE", null)); // too long
        Assert.False(captcha.Verify(c.Token, null, "twelve"));
        Assert.False(captcha.Verify(c.Token, null, "4"));
        Assert.False(captcha.Verify(c.Token, null, "eleven plus"));
        Assert.False(captcha.Verify(c.Token, null, "011x"));
        Assert.False(captcha.Verify(c.Token, null, "-11"));
        Assert.False(captcha.Verify(c.Token, new string('A', 5000), new string('1', 5000)));

        // Another challenge's answers don't fit this one, even with the same letters or sum.
        var other = captcha.Build("AC3DE", 4, 7);
        Assert.True(captcha.Verify(other.Token, "AC3DE", null));
        var different = captcha.Build("HJKMN", 1, 2);
        Assert.False(captcha.Verify(different.Token, "AC3DE", "11"));
    }

    [Fact]
    public void Expired_token_fails()
    {
        var (captcha, time) = Rig();
        var c = captcha.Build("AC3DE", 4, 7);

        time.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        Assert.True(captcha.Verify(c.Token, "AC3DE", null));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(captcha.Verify(c.Token, "AC3DE", null));
        Assert.False(captcha.Verify(c.Token, null, "11"));
        Assert.Equal(CaptchaResult.Invalid, captcha.Check(c.Token, "AC3DE", "11", out _, out _));
    }

    [Fact]
    public void A_token_names_its_challenge_and_says_when_it_expires()
    {
        var (captcha, time) = Rig();
        var c = captcha.Build("AC3DE", 4, 7);

        Assert.Equal(CaptchaResult.Right, captcha.Check(c.Token, "AC3DE", null, out var expires, out var id));
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromMinutes(10), expires);
        // A wrong answer still names the challenge (the guard uses up the token on any try); a made-up token names nothing.
        Assert.Equal(CaptchaResult.Wrong, captcha.Check(c.Token, "XXXXX", "1", out var expiresAgain, out var sameId));
        Assert.Equal((expires, id), (expiresAgain, sameId));
        captcha.Check(captcha.Build("AC3DE", 4, 7).Token, null, null, out _, out var otherId);
        Assert.NotEqual(id, otherId);
        Assert.Equal(CaptchaResult.Invalid, captcha.Check("junk", "AC3DE", "11", out _, out var none));
        Assert.Equal("", none);
    }

    [Fact]
    public void Tampered_token_fails_without_throwing()
    {
        var (captcha, _) = Rig();
        var c = captcha.Build("AC3DE", 4, 7);
        var tampered = new List<string?> { null, "", " ", "junk", "!!!!", c.Token[..^1], c.Token + "A", c.Token[..40], new string('A', 5000), new string('A', c.Token.Length) };

        // Each character changed in turn: a changed answer hash, expiry, nonce or signature all fail.
        for (var i = 0; i < c.Token.Length; i++)
        {
            var flipped = c.Token[i] == 'A' ? 'B' : 'A';
            tampered.Add(c.Token[..i] + flipped + c.Token[(i + 1)..]);
        }
        // Standard base64 and padding instead of base64url.
        tampered.Add(c.Token + "=");
        var standard = c.Token.Replace('-', '+').Replace('_', '/');
        if (standard != c.Token) tampered.Add(standard);
        tampered.Add(Convert.ToBase64String(new byte[120]));

        foreach (var token in tampered)
        {
            Assert.False(captcha.Verify(token, "AC3DE", "11"), token);
        }

        // A token made with another key is not ours.
        var elsewhere = new ImageCaptcha(new byte[32], new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero)));
        Assert.False(captcha.Verify(elsewhere.Build("AC3DE", 4, 7).Token, "AC3DE", "11"));
    }

    [Fact]
    public void A_proof_of_work_signature_is_not_a_captcha_token()
    {
        // The two share a key; the labels inside the signatures keep one from standing in for the other.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        var work = new ProofOfWork(Key, time);
        var captcha = new ImageCaptcha(Key, time);
        var c = work.Create(10);

        Assert.False(captcha.Verify(c.Signature, "AC3DE", "11"));
        Assert.False(captcha.Verify(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(c.Salt)), "AC3DE", "11"));
    }
}
