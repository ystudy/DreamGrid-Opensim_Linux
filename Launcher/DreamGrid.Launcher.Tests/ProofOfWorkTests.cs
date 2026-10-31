using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DreamGrid.Launcher.Core.Guests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The proof-of-work challenge in ALTCHA's format: making it, checking an answer, and refusing everything else.</summary>
public sealed class ProofOfWorkTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private static (ProofOfWork Work, FakeTimeProvider Time) Rig()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        return (new ProofOfWork(Key, time), time);
    }

    /// <summary>What the browser does: tries 0, 1, 2, ... until SHA-256(salt + number) is the challenge.</summary>
    internal static int Solve(string salt, string challenge, int maxNumber)
    {
        for (var n = 0; n <= maxNumber; n++)
            if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(salt + n))) == challenge) return n;
        throw new InvalidOperationException("No number solves the challenge.");
    }

    internal static string Payload(string algorithm, string challenge, int number, string salt, string signature) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["algorithm"] = algorithm, ["challenge"] = challenge, ["number"] = number, ["salt"] = salt, ["signature"] = signature,
        }));

    internal static string Answer(Challenge c, int? number = null) =>
        Payload(c.Algorithm, c.ChallengeHex, number ?? Solve(c.Salt, c.ChallengeHex, c.MaxNumber), c.Salt, c.Signature);

    [Fact]
    public void Solved_challenge_verifies()
    {
        var (work, _) = Rig();
        var c = work.Create(1000);

        Assert.Equal(("SHA-256", 1000), (c.Algorithm, c.MaxNumber));
        Assert.True(work.Verify(Answer(c)));
    }

    [Fact]
    public void Created_challenge_has_altchas_shape()
    {
        var (work, time) = Rig();
        var c = work.Create(1000);

        var match = Regex.Match(c.Salt, @"^[0-9a-f]{24}\?expires=(\d+)$");
        Assert.True(match.Success, c.Salt);
        Assert.Equal(time.GetUtcNow().AddMinutes(10).ToUnixTimeSeconds(), long.Parse(match.Groups[1].Value));
        Assert.Matches("^[0-9a-f]{64}$", c.ChallengeHex);
        Assert.Matches("^[0-9a-f]{64}$", c.Signature);
        Assert.NotEqual(work.Create(1000).Salt, c.Salt);
    }

    [Fact]
    public void Secret_number_covers_the_whole_range()
    {
        var (work, _) = Rig();
        var numbers = Enumerable.Range(0, 200).Select(_ => { var c = work.Create(1); return Solve(c.Salt, c.ChallengeHex, 1); }).ToHashSet();

        Assert.Equal([0, 1], numbers.Order());
    }

    [Fact]
    public void Tampered_number_refused()
    {
        var (work, _) = Rig();
        var c = work.Create(1000);
        var right = Solve(c.Salt, c.ChallengeHex, c.MaxNumber);

        Assert.False(work.Verify(Answer(c, right + 1)));
    }

    [Fact]
    public void Tampered_signature_refused()
    {
        var (work, _) = Rig();
        var c = work.Create(1000);
        var number = Solve(c.Salt, c.ChallengeHex, c.MaxNumber);

        // A signature made with another key, and one with a digit changed.
        var other = Convert.ToHexStringLower(HMACSHA256.HashData(new byte[32], Encoding.UTF8.GetBytes(c.ChallengeHex)));
        var flipped = (c.Signature[0] == '0' ? "1" : "0") + c.Signature[1..];
        Assert.False(work.Verify(Payload(c.Algorithm, c.ChallengeHex, number, c.Salt, other)));
        Assert.False(work.Verify(Payload(c.Algorithm, c.ChallengeHex, number, c.Salt, flipped)));
    }

    [Fact]
    public void Another_servers_challenge_refused()
    {
        var (work, time) = Rig();
        var elsewhere = new ProofOfWork(new byte[32], time);

        Assert.False(work.Verify(Answer(elsewhere.Create(1000))));
    }

    [Fact]
    public void A_made_up_salt_refused()
    {
        var (work, time) = Rig();
        // A client that makes its own challenge cannot sign it.
        var salt = "00" + $"?expires={time.GetUtcNow().AddMinutes(5).ToUnixTimeSeconds()}";
        var challenge = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(salt + "7")));

        Assert.False(work.Verify(Payload("SHA-256", challenge, 7, salt, new string('0', 64))));
    }

    [Fact]
    public void Expired_refused()
    {
        var (work, time) = Rig();
        var c = work.Create(1000);
        var answer = Answer(c);

        time.Advance(TimeSpan.FromMinutes(9));
        Assert.True(work.Verify(answer));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(work.Verify(answer));
    }

    [Fact]
    public void An_expiry_stretched_by_moving_a_digit_of_the_number_refused()
    {
        var (work, time) = Rig();
        // The challenge was made to expire already; its number has two digits, so "salt + number" can be cut one digit later.
        var salt = "aa?expires=" + time.GetUtcNow().AddMinutes(-1).ToUnixTimeSeconds();
        var number = 25;
        var made = work.Build(salt, number, 1000);
        var moved = salt + number.ToString()[0];

        Assert.False(work.Verify(Payload("SHA-256", made.ChallengeHex, 5, moved, made.Signature)));
        Assert.False(work.Verify(Answer(made)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 !!")]
    [InlineData("bm90IGpzb24=")] // "not json"
    [InlineData("W10=")] // []
    [InlineData("e30=")] // {}
    [InlineData("eyJudW1iZXIiOiJ4In0=")] // {"number":"x"}
    public void Garbage_payload_refused_without_throwing(string? payload)
    {
        var (work, _) = Rig();

        Assert.False(work.Verify(payload));
    }

    [Fact]
    public void Wrong_field_types_and_oversized_payloads_refused()
    {
        var (work, _) = Rig();
        var c = work.Create(1000);
        var n = Solve(c.Salt, c.ChallengeHex, c.MaxNumber);

        string Json(string number, string algorithm = "\"SHA-256\"") => Convert.ToBase64String(Encoding.UTF8.GetBytes(
            "{\"algorithm\":" + algorithm + ",\"challenge\":\"" + c.ChallengeHex + "\",\"number\":" + number
            + ",\"salt\":\"" + c.Salt + "\",\"signature\":\"" + c.Signature + "\"}"));

        Assert.True(work.Verify(Json(n.ToString())));
        Assert.False(work.Verify(Json("-1")));
        Assert.False(work.Verify(Json("1.5")));
        Assert.False(work.Verify(Json("99999999999999999999")));
        Assert.False(work.Verify(Json($"\"{n}\"")));
        Assert.False(work.Verify(Json(n.ToString(), "\"SHA-1\"")));
        Assert.False(work.Verify(Convert.ToBase64String(new byte[5000])));
    }

    [Fact]
    public void Matches_altcha_reference_vector()
    {
        var (work, time) = Rig();
        // ALTCHA: challenge = hex(SHA-256(salt + number)), signature = hex(HMAC-SHA-256(key, challenge)), the salt ending in ?expires=.
        var salt = "00112233445566778899aabb?expires=" + time.GetUtcNow().AddMinutes(10).ToUnixTimeSeconds();
        const int number = 12345;
        var challenge = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(salt + "12345")));
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(challenge)));

        var made = work.Build(salt, number, 50_000);

        Assert.Equal(new Challenge("SHA-256", challenge, 50_000, salt, signature), made);
        Assert.True(work.Verify(Payload("SHA-256", challenge, number, salt, signature)));
    }

    [Fact]
    public void Key_is_made_once_and_read_back()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dg-pow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "pow.key");

            var first = ProofOfWork.LoadOrCreateKey(file);
            var again = ProofOfWork.LoadOrCreateKey(file);

            Assert.Equal(32, first.Length);
            Assert.Equal(first, again);
            // A damaged file is replaced.
            File.WriteAllText(file, "damaged");
            var fresh = ProofOfWork.LoadOrCreateKey(file);
            Assert.Equal(32, fresh.Length);
            Assert.Equal(fresh, ProofOfWork.LoadOrCreateKey(file));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
