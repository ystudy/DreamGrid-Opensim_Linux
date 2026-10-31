using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DreamGrid.Launcher.Core.Guests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Single-use email tokens: 256 bits, bound to a purpose and an account, expiring, and kept only as hashes.</summary>
public sealed class EmailTokensTests : IDisposable
{
    private const string Verify = "verify-email";
    private const string Reset = "reset-password";

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-emailtokens-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly Guid _alice = Guid.NewGuid();
    private readonly Guid _bob = Guid.NewGuid();

    public EmailTokensTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string File_ => Path.Combine(_dir, "email-tokens.json");

    private EmailTokens New() => new(File_, _time, null);

    [Fact]
    public void Token_is_256_bit_base64url()
    {
        var token = New().Issue(Verify, _alice, Hour);

        // 32 bytes in base64url without padding is 43 characters from the URL-safe alphabet.
        Assert.Equal(43, token.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", token);
        Assert.Equal(32, Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "=").Length);
        Assert.NotEqual(token, New().Issue(Verify, _bob, Hour));
    }

    [Fact]
    public void Redeem_once_only()
    {
        var tokens = New();
        var token = tokens.Issue(Verify, _alice, Hour);

        Assert.True(tokens.TryRedeem(Verify, _alice, token));
        Assert.False(tokens.TryRedeem(Verify, _alice, token));
    }

    [Fact]
    public void Wrong_purpose_refused()
    {
        var tokens = New();
        var token = tokens.Issue(Verify, _alice, Hour);

        Assert.False(tokens.TryRedeem(Reset, _alice, token));
        // The mismatch spent it: a leaked token can't be retried against the right purpose.
        Assert.False(tokens.TryRedeem(Verify, _alice, token));
    }

    [Fact]
    public void Wrong_account_refused()
    {
        var tokens = New();
        var token = tokens.Issue(Verify, _alice, Hour);

        Assert.False(tokens.TryRedeem(Verify, _bob, token));
        // The mismatch spent it: a leaked token can't be retried against the right account.
        Assert.False(tokens.TryRedeem(Verify, _alice, token));
    }

    [Fact]
    public void Expired_refused()
    {
        var tokens = New();
        var token = tokens.Issue(Verify, _alice, Hour);

        _time.Advance(Hour);

        Assert.False(tokens.TryRedeem(Verify, _alice, token));
    }

    [Fact]
    public void Valid_until_the_moment_it_expires()
    {
        var tokens = New();
        var token = tokens.Issue(Verify, _alice, Hour);

        _time.Advance(Hour - TimeSpan.FromSeconds(1));

        Assert.True(tokens.TryRedeem(Verify, _alice, token));
    }

    [Fact]
    public void File_holds_no_token()
    {
        var token = New().Issue(Verify, _alice, Hour);

        var json = File.ReadAllText(File_);
        Assert.DoesNotContain(token, json, StringComparison.Ordinal);

        // Only the hash, purpose, principal and expiry are kept.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        using var doc = JsonDocument.Parse(json);
        var entry = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal(hash, entry.GetProperty("hash").GetString(), ignoreCase: true);
        Assert.Equal(Verify, entry.GetProperty("purpose").GetString());
        Assert.Equal(_alice, entry.GetProperty("principal").GetGuid());
        Assert.Equal(_time.GetUtcNow() + Hour, entry.GetProperty("expires").GetDateTimeOffset());
        Assert.Equal(4, entry.EnumerateObject().Count());
    }

    [Fact]
    public void Survives_restart()
    {
        var token = New().Issue(Verify, _alice, Hour);

        var reloaded = New();

        Assert.True(reloaded.TryRedeem(Verify, _alice, token));
        Assert.False(New().TryRedeem(Verify, _alice, token));
    }

    [Fact]
    public void New_token_replaces_old()
    {
        var tokens = New();
        var first = tokens.Issue(Verify, _alice, Hour);
        var other = tokens.Issue(Reset, _alice, Hour);
        var bobs = tokens.Issue(Verify, _bob, Hour);
        var second = tokens.Issue(Verify, _alice, Hour);

        Assert.False(tokens.TryRedeem(Verify, _alice, first));
        Assert.True(tokens.TryRedeem(Verify, _alice, second));
        // Other purposes and other accounts are not touched.
        Assert.True(tokens.TryRedeem(Reset, _alice, other));
        Assert.True(tokens.TryRedeem(Verify, _bob, bobs));
    }

    [Fact]
    public void Expired_entries_are_dropped_when_the_file_is_written()
    {
        var tokens = New();
        tokens.Issue(Verify, _alice, Hour);
        _time.Advance(TimeSpan.FromHours(2));

        tokens.Issue(Verify, _bob, Hour);

        using var doc = JsonDocument.Parse(File.ReadAllText(File_));
        var entry = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal(_bob, entry.GetProperty("principal").GetGuid());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 at all !!!")]
    [InlineData("%%%%====")]
    [InlineData("\ud800 lone surrogate")]
    public void Malformed_token_refused_without_throwing(string bad)
    {
        var tokens = New();
        var token = tokens.Issue(Verify, _alice, Hour);

        Assert.False(tokens.TryRedeem(Verify, _alice, bad));
        Assert.False(tokens.TryRedeem(Verify, _alice, null));
        // A guess that matches nothing does not spend the real token.
        Assert.True(tokens.TryRedeem(Verify, _alice, token));
    }

    [Fact]
    public void A_megabyte_token_is_refused_without_throwing()
    {
        var tokens = New();

        Assert.False(tokens.TryRedeem(Verify, _alice, new string('A', 1024 * 1024)));
    }

    [Fact]
    public void Damaged_file_means_no_tokens()
    {
        File.WriteAllText(File_, "{ this is not json");

        var tokens = New();

        Assert.False(tokens.TryRedeem(Verify, _alice, "anything"));
        Assert.True(tokens.Issue(Verify, _alice, Hour).Length > 0);
    }
}
