using System.Net;
using DreamGrid.Launcher.Core.Security;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class ResetLimitsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dgtest-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private static readonly IPAddress Ip = IPAddress.Parse("192.168.1.20");

    public ResetLimitsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string File => Path.Combine(_dir, "reset-limits.json");

    [Fact]
    public void Fourth_send_in_an_hour_refused_per_account()
    {
        var limits = new ResetLimits(_time, File);
        var who = Guid.NewGuid();
        for (var i = 0; i < 3; i++)
        {
            Assert.True(limits.SendAllowed(who, $"ann{i}@x.com"));
            limits.CountSend(who, $"ann{i}@x.com");
        }
        Assert.False(limits.SendAllowed(who, "fresh@x.com"));
        Assert.True(limits.SendAllowed(Guid.NewGuid(), "fresh@x.com"));
    }

    [Fact]
    public void Same_email_with_other_names_is_one_count()
    {
        var limits = new ResetLimits(_time, File);
        limits.CountSend(Guid.NewGuid(), "Ann@X.com ");
        limits.CountSend(Guid.NewGuid(), "ann@x.com");
        limits.CountSend(Guid.NewGuid(), " ANN@x.COM");
        Assert.False(limits.SendAllowed(Guid.NewGuid(), "ann@x.com"));
    }

    [Fact]
    public void Eleventh_request_from_an_address_refused()
    {
        var limits = new ResetLimits(_time, File);
        for (var i = 0; i < 10; i++)
        {
            Assert.True(limits.AddressAllowed(Ip));
            limits.CountAddress(Ip);
        }
        Assert.False(limits.AddressAllowed(Ip));
        Assert.False(limits.AddressAllowed(IPAddress.Parse("::ffff:192.168.1.20")));
        Assert.True(limits.AddressAllowed(IPAddress.Parse("192.168.1.21")));
    }

    [Fact]
    public void An_unknown_address_is_never_limited()
    {
        var limits = new ResetLimits(_time, File);
        for (var i = 0; i < 20; i++) limits.CountAddress(null);
        Assert.True(limits.AddressAllowed(null));
    }

    [Fact]
    public void Window_rolls()
    {
        var limits = new ResetLimits(_time, File);
        var who = Guid.NewGuid();
        for (var i = 0; i < 3; i++) limits.CountSend(who, "a@x.com");
        _time.Advance(TimeSpan.FromMinutes(30));
        Assert.False(limits.SendAllowed(who, "b@x.com"));
        _time.Advance(TimeSpan.FromMinutes(31));
        Assert.True(limits.SendAllowed(who, "b@x.com"));
    }

    [Fact]
    public void Limits_survive_restart()
    {
        var who = Guid.NewGuid();
        var first = new ResetLimits(_time, File);
        for (var i = 0; i < 3; i++) first.CountSend(who, "a@x.com");
        for (var i = 0; i < 10; i++) first.CountAddress(Ip);

        var second = new ResetLimits(_time, File);
        Assert.False(second.SendAllowed(who, "b@x.com"));
        Assert.False(second.SendAllowed(Guid.NewGuid(), "a@x.com"));
        Assert.False(second.AddressAllowed(Ip));
    }

    [Fact]
    public void File_holds_no_typed_text()
    {
        var who = Guid.NewGuid();
        var limits = new ResetLimits(_time, File);
        limits.CountSend(who, "secret.person@example.com");
        limits.CountAddress(Ip);

        var text = System.IO.File.ReadAllText(File);
        Assert.DoesNotContain("secret.person", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("192.168.1.20", text);
        Assert.DoesNotContain(who.ToString(), text, StringComparison.OrdinalIgnoreCase);
    }
}
