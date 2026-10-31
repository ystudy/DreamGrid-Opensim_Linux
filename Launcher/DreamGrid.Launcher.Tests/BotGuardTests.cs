using System.Net;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The bot-protection layers in order: honeypot, per-address limit, proof-of-work, image CAPTCHA.</summary>
public sealed class BotGuardTests
{
    private sealed class ListLog : ILogger<BotGuard>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    private sealed class Rig
    {
        public BotSettings Bots { get; set; }
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        public ListLog Log { get; } = new();
        public ProofOfWork Work { get; }
        public ImageCaptcha Captcha { get; }
        public BotGuard Guard { get; }

        public Rig(BotSettings bots)
        {
            Bots = bots;
            Work = new ProofOfWork(new byte[32], Time);
            Captcha = new ImageCaptcha(new byte[32], Time);
            Guard = new BotGuard(() => TestSettings.Create() with { Bots = Bots }, Work, Captcha, Time, Log);
        }

        public string FreshAnswer() => ProofOfWorkTests.Answer(Work.Create(1000));
    }

    /// <summary>Every layer off; a test switches on the one it is about.</summary>
    private static readonly BotSettings Off = new(Honeypot: false, RateLimit: false, ProofOfWork: false, ImageCaptcha: false);

    private static HttpContext Post(string ip = "198.51.100.7", string? website = null, string? pow = null,
        string? token = null, string? letters = null, string? answer = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        ctx.Request.Method = "POST";
        ctx.Request.ContentType = "application/x-www-form-urlencoded";
        var fields = new Dictionary<string, StringValues>();
        if (website is not null) fields["website"] = website;
        if (pow is not null) fields["pow"] = pow;
        if (token is not null) fields["captcha-token"] = token;
        if (letters is not null) fields["captcha-letters"] = letters;
        if (answer is not null) fields["captcha-answer"] = answer;
        ctx.Request.Form = new FormCollection(fields);
        return ctx;
    }

    [Fact]
    public void Honeypot_filled_fails()
    {
        var rig = new Rig(Off with { Honeypot = true });

        Assert.Equal(new BotCheck(true, null), rig.Guard.Check(Post(website: ""), "signup"));
        Assert.Equal(new BotCheck(true, null), rig.Guard.Check(Post(), "signup"));
        Assert.Equal(new BotCheck(false, "honeypot"), rig.Guard.Check(Post(website: "http://spam.example"), "signup"));
    }

    [Fact]
    public void Sixth_post_in_an_hour_fails_when_limit_5()
    {
        var rig = new Rig(Off with { RateLimit = true, PerHourPerIp = 5 });

        for (var i = 0; i < 5; i++)
            Assert.True(rig.Guard.Check(Post(), "signup").Passed, $"post {i + 1}");
        Assert.Equal(new BotCheck(false, "rate-limit"), rig.Guard.Check(Post(), "signup"));

        // Another address and another form have their own count.
        Assert.True(rig.Guard.Check(Post("198.51.100.8"), "signup").Passed);
        Assert.True(rig.Guard.Check(Post(), "try").Passed);

        // The hour passes.
        rig.Time.Advance(TimeSpan.FromMinutes(61));
        Assert.True(rig.Guard.Check(Post(), "signup").Passed);
    }

    [Fact]
    public void An_address_written_two_ways_is_one_address()
    {
        var rig = new Rig(Off with { RateLimit = true, PerHourPerIp = 1 });

        Assert.True(rig.Guard.Check(Post("198.51.100.7"), "signup").Passed);
        Assert.Equal("rate-limit", rig.Guard.Check(Post("::ffff:198.51.100.7"), "signup").Layer);
    }

    [Fact]
    public void The_limit_slides_rather_than_resetting_on_the_hour()
    {
        var rig = new Rig(Off with { RateLimit = true, PerHourPerIp = 2 });

        Assert.True(rig.Guard.Check(Post(), "signup").Passed); // 12:00
        rig.Time.Advance(TimeSpan.FromMinutes(30));
        Assert.True(rig.Guard.Check(Post(), "signup").Passed); // 12:30
        Assert.False(rig.Guard.Check(Post(), "signup").Passed);
        rig.Time.Advance(TimeSpan.FromMinutes(31)); // 13:01: the 12:00 post is out, the 12:30 one is in
        Assert.True(rig.Guard.Check(Post(), "signup").Passed);
        Assert.False(rig.Guard.Check(Post(), "signup").Passed);
    }

    [Fact]
    public void A_post_stopped_by_another_layer_does_not_count_against_the_limit()
    {
        var rig = new Rig(Off with { Honeypot = true, RateLimit = true, PerHourPerIp = 1 });

        Assert.False(rig.Guard.Check(Post(website: "x"), "signup").Passed);
        Assert.True(rig.Guard.Check(Post(), "signup").Passed);
        Assert.Equal("rate-limit", rig.Guard.Check(Post(), "signup").Layer);
    }

    [Fact]
    public void Reused_pow_payload_fails()
    {
        var rig = new Rig(Off with { ProofOfWork = true });
        var answer = rig.FreshAnswer();

        Assert.True(rig.Guard.Check(Post(pow: answer), "signup").Passed);
        Assert.Equal(new BotCheck(false, "proof-of-work"), rig.Guard.Check(Post(pow: answer), "signup"));
        // A different answer is fine; none or a made-up one is not.
        Assert.True(rig.Guard.Check(Post(pow: rig.FreshAnswer()), "signup").Passed);
        Assert.Equal("proof-of-work", rig.Guard.Check(Post(), "signup").Layer);
        Assert.Equal("proof-of-work", rig.Guard.Check(Post(pow: "bm90IGpzb24="), "signup").Layer);
    }

    [Fact]
    public void A_solved_challenge_is_used_once_however_the_answer_is_written()
    {
        var rig = new Rig(Off with { ProofOfWork = true });
        var c = rig.Work.Create(1000);
        var n = ProofOfWorkTests.Solve(c.Salt, c.ChallengeHex, c.MaxNumber);
        var answer = ProofOfWorkTests.Answer(c);
        Assert.True(rig.Guard.Check(Post(pow: answer), "signup").Passed);

        // The same answer with whitespace in the base64 (which the decoder ignores), with the JSON fields reordered and spaced, and
        // with both: each reaches the same solved challenge and is refused.
        var spaced = answer[..10] + " \r\n\t" + answer[10..];
        var json = "{ \"signature\": \"" + c.Signature + "\", \"salt\": \"" + c.Salt + "\", \"number\": " + n
            + ", \"challenge\": \"" + c.ChallengeHex + "\", \"algorithm\": \"SHA-256\" }";
        var reordered = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        var both = reordered[..7] + "\n" + reordered[7..];
        foreach (var variant in new[] { spaced, reordered, both })
            Assert.Equal(new BotCheck(false, "proof-of-work"), rig.Guard.Check(Post(pow: variant), "signup"));
        // They were refused as used, not as invalid: on their own they are good answers.
        foreach (var variant in new[] { spaced, reordered, both })
            Assert.True(new Rig(Off with { ProofOfWork = true }).Guard.Check(Post(pow: variant), "signup").Passed);
    }

    [Fact]
    public void An_expired_pow_payload_is_forgotten_and_still_refused()
    {
        var rig = new Rig(Off with { ProofOfWork = true });
        var answer = rig.FreshAnswer();
        Assert.True(rig.Guard.Check(Post(pow: answer), "signup").Passed);

        rig.Time.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal("proof-of-work", rig.Guard.Check(Post(pow: answer), "signup").Layer);
    }

    [Fact]
    public void A_failed_pow_does_not_use_up_a_rate_limit_slot()
    {
        var rig = new Rig(Off with { RateLimit = true, PerHourPerIp = 1, ProofOfWork = true });

        Assert.Equal("proof-of-work", rig.Guard.Check(Post(pow: "nope"), "signup").Layer);
        Assert.True(rig.Guard.Check(Post(pow: rig.FreshAnswer()), "signup").Passed);
        Assert.Equal("rate-limit", rig.Guard.Check(Post(pow: rig.FreshAnswer()), "signup").Layer);
    }

    [Fact]
    public void Layers_off_are_skipped()
    {
        var rig = new Rig(Off);

        // Everything a bot could get wrong is wrong, and nothing is checked.
        for (var i = 0; i < 20; i++)
            Assert.Equal(new BotCheck(true, null), rig.Guard.Check(Post(website: "filled", pow: "junk"), "signup"));
        Assert.Empty(rig.Log.Lines);

        // Switching a layer on takes effect on the next post.
        rig.Bots = Off with { Honeypot = true };
        Assert.Equal("honeypot", rig.Guard.Check(Post(website: "filled"), "signup").Layer);
    }

    [Fact]
    public void A_post_without_a_form_body_does_not_throw()
    {
        var rig = new Rig(new BotSettings());
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;

        Assert.Equal("proof-of-work", rig.Guard.Check(ctx, "signup").Layer);
    }

    [Fact]
    public void Failure_log_has_no_field_values()
    {
        var rig = new Rig(new BotSettings());

        rig.Guard.Check(Post(website: "SECRET-HONEY-TEXT", pow: "SECRET-POW-PAYLOAD"), "signup");
        rig.Guard.Check(Post(pow: "SECRET-POW-PAYLOAD"), "signup");

        Assert.Equal(2, rig.Log.Lines.Count);
        Assert.Contains("signup", rig.Log.Lines[0]);
        Assert.Contains("honeypot", rig.Log.Lines[0]);
        Assert.Contains("proof-of-work", rig.Log.Lines[1]);
        Assert.DoesNotContain(rig.Log.Lines, l => l.Contains("SECRET"));
    }

    [Fact]
    public void Captcha_layer_only_when_on()
    {
        var rig = new Rig(Off);

        // Off: no token, junk, whatever: not checked.
        Assert.Equal(new BotCheck(true, null), rig.Guard.Check(Post(token: "junk", letters: "nope", answer: "nope"), "signup"));
        Assert.Equal(new BotCheck(true, null), rig.Guard.Check(Post(), "signup"));
        Assert.Empty(rig.Log.Lines);

        // On: it is the last layer, and it names itself.
        rig.Bots = Off with { ImageCaptcha = true };
        Assert.Equal(new BotCheck(false, "image-captcha"), rig.Guard.Check(Post(), "signup"));
        Assert.Equal("image-captcha", rig.Guard.Check(Post(token: "junk", letters: "AC3DE", answer: "11"), "signup").Layer);

        var c = rig.Captcha.Build("AC3DE", 4, 7);
        Assert.Equal(new BotCheck(true, null), rig.Guard.Check(Post(token: c.Token, letters: "ac3de"), "signup"));
        var d = rig.Captcha.Build("AC3DE", 4, 7);
        Assert.Equal(new BotCheck(true, null), rig.Guard.Check(Post(token: d.Token, answer: "Eleven"), "signup"));

        // The layers before it still decide first.
        rig.Bots = Off with { Honeypot = true, ImageCaptcha = true };
        var e = rig.Captcha.Build("AC3DE", 4, 7);
        Assert.Equal("honeypot", rig.Guard.Check(Post(website: "x", token: e.Token, letters: "AC3DE"), "signup").Layer);
        rig.Bots = Off with { ProofOfWork = true, ImageCaptcha = true };
        Assert.Equal("proof-of-work", rig.Guard.Check(Post(token: e.Token, letters: "AC3DE"), "signup").Layer);
        Assert.Equal(new BotCheck(true, null), rig.Guard.Check(Post(pow: rig.FreshAnswer(), token: e.Token, letters: "AC3DE"), "signup"));
    }

    [Fact]
    public void Reused_captcha_token_fails()
    {
        var rig = new Rig(Off with { ImageCaptcha = true });
        var c = rig.Captcha.Build("AC3DE", 4, 7);

        Assert.True(rig.Guard.Check(Post(token: c.Token, letters: "AC3DE"), "signup").Passed);
        Assert.Equal(new BotCheck(false, "image-captcha"), rig.Guard.Check(Post(token: c.Token, letters: "AC3DE"), "signup"));
        Assert.Equal("image-captcha", rig.Guard.Check(Post(token: c.Token, answer: "11"), "signup").Layer);
        // The same token on another form is refused too: it is the token that is spent, not the form.
        Assert.Equal("image-captcha", rig.Guard.Check(Post(token: c.Token, letters: "AC3DE"), "try").Layer);

        // Spent until it would have expired anyway, and expired tokens are refused on their own.
        rig.Time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal("image-captcha", rig.Guard.Check(Post(token: c.Token, letters: "AC3DE"), "signup").Layer);
        Assert.True(rig.Guard.Check(Post(token: rig.Captcha.Build("AC3DE", 4, 7).Token, letters: "AC3DE"), "signup").Passed);
    }

    [Fact]
    public void A_wrong_captcha_answer_uses_up_the_token()
    {
        var rig = new Rig(Off with { ImageCaptcha = true });
        var c = rig.Captcha.Build("AC3DE", 4, 7);

        // One try per picture, so the sum (eighteen possible answers) can't be guessed by trying them all on one token.
        Assert.Equal("image-captcha", rig.Guard.Check(Post(token: c.Token, answer: "5"), "signup").Layer);
        Assert.Equal("image-captcha", rig.Guard.Check(Post(token: c.Token, answer: "11"), "signup").Layer);
        Assert.Equal("image-captcha", rig.Guard.Check(Post(token: c.Token, letters: "AC3DE"), "signup").Layer);
    }

    [Fact]
    public void A_failed_captcha_does_not_use_up_a_rate_limit_slot()
    {
        var rig = new Rig(Off with { RateLimit = true, PerHourPerIp = 1, ImageCaptcha = true });

        Assert.Equal("image-captcha", rig.Guard.Check(Post(), "signup").Layer);
        Assert.True(rig.Guard.Check(Post(token: rig.Captcha.Build("AC3DE", 4, 7).Token, letters: "AC3DE"), "signup").Passed);
        Assert.Equal("rate-limit", rig.Guard.Check(Post(token: rig.Captcha.Build("AC3DE", 4, 7).Token, letters: "AC3DE"), "signup").Layer);
    }

    [Fact]
    public void Captcha_failure_log_has_no_answers()
    {
        var rig = new Rig(Off with { ImageCaptcha = true });
        var c = rig.Captcha.Build("AC3DE", 4, 7);

        rig.Guard.Check(Post(token: c.Token, letters: "SECRET-LETTERS", answer: "SECRET-SUM"), "signup");
        rig.Guard.Check(Post(token: "SECRET-TOKEN"), "signup");

        Assert.Equal(2, rig.Log.Lines.Count);
        Assert.All(rig.Log.Lines, l => Assert.Contains("image-captcha", l));
        Assert.DoesNotContain(rig.Log.Lines, l => l.Contains("SECRET") || l.Contains(c.Token));
    }
}
