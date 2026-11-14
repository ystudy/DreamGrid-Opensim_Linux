using System.Security.Cryptography.X509Certificates;
using GridGate.Launcher.Core.Money;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GridGate.Launcher.Tests;

public sealed class MoneyCertificateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gg-money-cert-" + Guid.NewGuid().ToString("N"));
    private string Pfx => Path.Combine(_dir, "money.pfx");

    public MoneyCertificateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Creates_then_reuses()
    {
        var time = Clock();
        using var first = MoneyCertificate.LoadOrCreate(Pfx, time);
        using var second = MoneyCertificate.LoadOrCreate(Pfx, time);

        Assert.True(File.Exists(Pfx));
        Assert.Equal(MoneyCertificate.Fingerprint(first), MoneyCertificate.Fingerprint(second));
        Assert.True(second.HasPrivateKey);
    }

    [Fact]
    public void Lasts_five_years()
    {
        using var c = MoneyCertificate.LoadOrCreate(Pfx, Clock());

        Assert.Equal(2031, c.NotAfter.ToUniversalTime().Year);
        Assert.Equal("CN=GridGate money", c.Subject);
    }

    [Fact]
    public void Renews_30_days_before_expiry()
    {
        var time = Clock();
        using var first = MoneyCertificate.LoadOrCreate(Pfx, time);

        time.Advance(TimeSpan.FromDays(5 * 365 - 25)); // 4 years 11 months and some days: under 30 days left
        using var renewed = MoneyCertificate.LoadOrCreate(Pfx, time);

        Assert.NotEqual(MoneyCertificate.Fingerprint(first), MoneyCertificate.Fingerprint(renewed));
    }

    [Fact]
    public void Has_loopback_names()
    {
        using var c = MoneyCertificate.LoadOrCreate(Pfx, Clock());
        var san = c.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();

        Assert.Contains("localhost", san.EnumerateDnsNames());
        var ips = san.EnumerateIPAddresses().Select(ip => ip.ToString()).ToList();
        Assert.Contains("127.0.0.1", ips);
        Assert.Contains("::1", ips);
    }

    [Fact]
    public void Fingerprint_is_64_hex_chars()
    {
        using var c = MoneyCertificate.LoadOrCreate(Pfx, Clock());

        Assert.Matches("^[0-9A-F]{64}$", MoneyCertificate.Fingerprint(c));
    }
}
