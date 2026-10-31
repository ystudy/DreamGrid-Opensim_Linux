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

/// <summary>The bot-protection layers in order: honeypot, per-address limit, proof-of-work.</summary>
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
        public BotGuard Guard { get; }

        public Rig(BotSettings bots)
        {
            Bots = bots;
            Work = new ProofOfWork(new byte[32], Time);
            Guard = new BotGuard(() => TestSettings.Create() with { Bots = Bots }, Work, Time, Log);
        }

        public string FreshAnswer() => ProofOfWorkTests.Answer(Work.Create(1000));
    }

    /// <summary>Every layer off; a test switches on the one it is about.</summary>
    private static readonly BotSettings Off = new(Honeypot: false, RateLimit: false, ProofOfWork: false);

    private static HttpContext Post(string ip = "198.51.100.7", string? website = null, string? pow = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        ctx.Request.Method = "POST";
        ctx.Request.ContentType = "application/x-www-form-urlencoded";
        var fields = new Dictionary<string, StringValues>();
        if (website is not null) fields["website"] = website;
        if (pow is not null) fields["pow"] = pow;
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
}
